using System.Text;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Options;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>What the caller sent us; the two transports (GET query, POST form) collapse into this.</summary>
public sealed record SabRequest
{
    public string Mode { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Value { get; init; }

    /// <summary>The second argument some modes take, such as the new category for change_cat.</summary>
    public string? Value2 { get; init; }

    public string? Category { get; init; }
    public int? Priority { get; init; }
    public bool DeleteFiles { get; init; }
    public string? NzbName { get; init; }
    public byte[]? UploadedFile { get; init; }
    public string? ApiKey { get; init; }
}

/// <summary>
/// Enough of the SABnzbd API for Sonarr and Radarr to treat this service as a download client.
/// Everything is answered from our own queue; there is no Usenet anywhere behind it.
/// </summary>
public class SabnzbdHandler(
    IDbContextFactory<AppDbContext> dbFactory,
    DownloadService downloads,
    DownloadManager manager,
    SettingsService settingsService,
    SpeedLimitService speedLimits,
    IHttpClientFactory httpClientFactory,
    TimeProvider clock,
    ILogger<SabnzbdHandler> logger)
{
    /// <summary>Reported to clients; they gate features on it, so it must look like a modern SAB.</summary>
    public const string ReportedVersion = "4.3.3";

    public async Task<object> HandleAsync(SabRequest request, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync(ct);

        // "version" is the unauthenticated probe clients use before they have a key.
        var mode = request.Mode.ToLowerInvariant();
        if (mode == "version")
            return new { version = ReportedVersion };

        if (!string.Equals(request.ApiKey, settings.ApiKey, StringComparison.Ordinal))
            return new { status = false, error = "API Key Incorrect" };

        return mode switch
        {
            "auth" => new { auth = "apikey" },
            "get_config" => await GetConfigAsync(ct),
            "get_cats" => new { categories = settings.Categories.Select(x => x.Name).ToArray() },
            "change_cat" => await ChangeCategoryAsync(request, ct),
            "queue" => await QueueAsync(request, ct),
            "history" => await HistoryAsync(request, ct),
            "addfile" => await AddFileAsync(request, ct),
            "addurl" => await AddUrlAsync(request, ct),
            "pause" => await SetPausedAsync(true, ct),
            "resume" => await SetPausedAsync(false, ct),
            "config" => await ConfigAsync(request, ct),
            "fullstatus" or "status" => await FullStatusAsync(ct),
            _ => new { status = false, error = $"Unsupported mode: {request.Mode}" }
        };
    }

    private async Task<object> GetConfigAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);

        // A relative dir is joined to complete_dir by the client, exactly as the downloader
        // lays finished files out, so the clients find them without a path mapping.
        var categories = settings.Categories
            .Select((category, order) => (object)new
            {
                name = category.Name,
                order,
                pp = "3",
                script = "None",
                dir = DownloadCategories.ReportedFolder(category, settings.PathMappings),
                newzbin = string.Empty,
                priority = category.Priority
            })
            .ToList();

        return new
        {
            config = new
            {
                misc = new
                {
                    // Reported through the path mappings: the client has to be told where it
                    // sees these files, not where we do.
                    complete_dir = PathMapper.Apply(settings.CompletePath, settings.PathMappings),
                    download_dir = PathMapper.Apply(settings.IncompletePath, settings.PathMappings),
                    pre_check = 0,
                    history_retention = "0",
                    // Our own naming is already what the clients want, so SAB-side sorting stays off.
                    enable_tv_sorting = 0,
                    enable_movie_sorting = 0,
                    enable_date_sorting = 0,
                    tv_categories = new[] { string.Empty },
                    movie_categories = new[] { string.Empty },
                    date_categories = new[] { string.Empty }
                },
                categories,
                servers = Array.Empty<object>(),
                sorters = Array.Empty<object>()
            }
        };
    }

    /// <summary>SABnzbd's <c>mode=change_cat&amp;value=nzo_id&amp;value2=category</c>.</summary>
    private async Task<object> ChangeCategoryAsync(SabRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
            return new { status = false, error = "No nzo id supplied" };

        var changed = false;
        foreach (var nzoId in SplitValues(request.Value))
            changed |= await downloads.ChangeCategoryAsync(nzoId, request.Value2, ct);

        return new { status = changed };
    }

    private async Task<object> QueueAsync(SabRequest request, CancellationToken ct)
    {
        switch (request.Name?.ToLowerInvariant())
        {
            case "delete":
            {
                // SABnzbd accepts the literal "all" here as well as a list of nzo ids,
                // and that is what a client sends when it clears the whole queue.
                var values = SplitValues(request.Value);

                if (values.Contains("all", StringComparer.OrdinalIgnoreCase))
                {
                    await downloads.RemoveAllFromQueueAsync(request.DeleteFiles, ct);
                    return new { status = true };
                }

                foreach (var nzoId in values)
                    await downloads.RemoveFromQueueAsync(nzoId, request.DeleteFiles, ct);

                return new { status = true };
            }

            case "pause":
                foreach (var nzoId in SplitValues(request.Value))
                    await downloads.PauseAsync(nzoId, ct);
                return new { status = true };

            case "resume":
                foreach (var nzoId in SplitValues(request.Value))
                    await downloads.ResumeAsync(nzoId, ct);
                return new { status = true };

            case "purge":
                await downloads.SetAllPausedAsync(false, ct);
                return new { status = true };
        }

        return await BuildQueueAsync(ct);
    }

    private async Task<object> BuildQueueAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Downloads
            .AsNoTracking()
            .Where(x => x.Status == DownloadStatus.Queued
                        || x.Status == DownloadStatus.Downloading
                        || x.Status == DownloadStatus.Paused)
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.QueuedAt)
            .ToListAsync(ct);

        var slots = new List<object>(rows.Count);
        long totalBytes = 0;
        long remainingBytes = 0;
        var index = 0;

        foreach (var row in rows)
        {
            // The database row lags a couple of seconds behind; live state is more useful here.
            var live = manager.Get(row.Id);
            var downloaded = live is not null ? Interlocked.Read(ref live.DownloadedBytes) : row.DownloadedBytes;
            var total = Math.Max(row.TotalBytes, live is not null ? Interlocked.Read(ref live.TotalBytes) : 0);
            var rate = live is not null ? Interlocked.Read(ref live.BytesPerSecond) : 0;
            var left = Math.Max(0, total - downloaded);

            totalBytes += total;
            remainingBytes += left;

            slots.Add(new
            {
                status = StatusName(row.Status),
                index = index++,
                nzo_id = row.NzoId,
                unpackopts = "3",
                priority = SabPriority.Name(row.Priority),
                cat = string.IsNullOrWhiteSpace(row.Category) ? "*" : row.Category,
                filename = row.Name,
                labels = Array.Empty<string>(),
                percentage = SabFormat.Percentage(downloaded, total),
                size = SabFormat.Size(total),
                sizeleft = SabFormat.Size(left),
                mb = SabFormat.Megabytes(total),
                mbleft = SabFormat.Megabytes(left),
                timeleft = SabFormat.TimeLeft(left, rate),
                avg_age = AverageAge(row.QueuedAt),
                script = "None",
                direct_unpack = (string?)null,
                password = string.Empty,
                missing = 0
            });
        }

        var speed = manager.TotalBytesPerSecond();
        var paused = speedLimits.Current.Paused;

        return new
        {
            queue = new
            {
                status = paused ? "Paused" : rows.Any(x => x.Status == DownloadStatus.Downloading) ? "Downloading" : "Idle",
                speedlimit = settings.ManualSpeedLimitKibps.ToString(),
                speedlimit_abs = (settings.ManualSpeedLimitKibps * 1024L).ToString(),
                paused,
                noofslots_total = slots.Count,
                noofslots = slots.Count,
                limit = slots.Count,
                start = 0,
                timeleft = SabFormat.TimeLeft(remainingBytes, speed),
                speed = SabFormat.Speed(speed),
                kbpersec = SabFormat.KiloBytesPerSecond(speed),
                size = SabFormat.Size(totalBytes),
                sizeleft = SabFormat.Size(remainingBytes),
                mb = SabFormat.Megabytes(totalBytes),
                mbleft = SabFormat.Megabytes(remainingBytes),
                slots,
                diskspace1 = DiskFreeGb(settings.CompletePath),
                diskspace2 = DiskFreeGb(settings.IncompletePath),
                diskspacetotal1 = DiskTotalGb(settings.CompletePath),
                diskspacetotal2 = DiskTotalGb(settings.IncompletePath),
                have_warnings = "0",
                pause_int = "0",
                left_quota = "0",
                version = ReportedVersion,
                finish = 0,
                cache_art = "0",
                cache_size = "0"
            }
        };
    }

    private async Task<object> HistoryAsync(SabRequest request, CancellationToken ct)
    {
        if (string.Equals(request.Name, "delete", StringComparison.OrdinalIgnoreCase))
        {
            var values = SplitValues(request.Value);

            if (values.Contains("all", StringComparer.OrdinalIgnoreCase))
            {
                await downloads.PurgeHistoryAsync(request.DeleteFiles, ct);
                return new { status = true };
            }

            foreach (var nzoId in values)
                await downloads.RemoveFromHistoryAsync(nzoId, request.DeleteFiles, ct);

            return new { status = true };
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await settingsService.GetAsync(ct);

        var rows = await db.Downloads
            .AsNoTracking()
            .Where(x => x.Status == DownloadStatus.Completed
                        || x.Status == DownloadStatus.Failed
                        || x.Status == DownloadStatus.Deleted)
            .OrderByDescending(x => x.CompletedAt)
            .Take(500)
            .ToListAsync(ct);

        var slots = rows.Select(row => new
        {
            id = row.Id,
            completed = (row.CompletedAt ?? row.QueuedAt).ToUnixTimeSeconds(),
            name = row.Name,
            nzb_name = row.Name + ".nzb",
            category = string.IsNullOrWhiteSpace(row.Category) ? "*" : row.Category,
            pp = "D",
            script = "None",
            report = string.Empty,
            url = string.Empty,
            status = StatusName(row.Status),
            nzo_id = row.NzoId,
            // Where the client will find the finished folder, which is what it imports from.
            storage = PathMapper.Apply(row.CompletedPath, settings.PathMappings),
            path = PathMapper.Apply(row.CompletedPath, settings.PathMappings),
            script_log = string.Empty,
            script_line = string.Empty,
            download_time = (long)((row.CompletedAt ?? row.QueuedAt) - (row.StartedAt ?? row.QueuedAt)).TotalSeconds,
            postproc_time = 0,
            stage_log = Array.Empty<object>(),
            downloaded = row.DownloadedBytes,
            completeness = 100,
            fail_message = row.FailureMessage ?? string.Empty,
            url_info = string.Empty,
            bytes = row.DownloadedBytes > 0 ? row.DownloadedBytes : row.TotalBytes,
            meta = (object?)null,
            series = string.Empty,
            md5sum = string.Empty,
            password = string.Empty,
            action_line = string.Empty,
            size = SabFormat.Size(row.DownloadedBytes > 0 ? row.DownloadedBytes : row.TotalBytes),
            loaded = false,
            retry = 0
        }).ToList();

        return new
        {
            history = new
            {
                noofslots = slots.Count,
                ppslots = 0,
                day_size = "0",
                week_size = "0",
                month_size = "0",
                total_size = "0",
                last_history_update = clock.GetUtcNow().ToUnixTimeSeconds(),
                slots
            }
        };
    }

    /// <summary>
    /// Accepts the pseudo-nzb a client grabbed from our Newznab endpoint and turns it back
    /// into a queued download.
    /// </summary>
    private async Task<object> AddFileAsync(SabRequest request, CancellationToken ct)
    {
        if (request.UploadedFile is not { Length: > 0 } bytes)
            return new { status = false, error = "No nzb file was posted" };

        var xml = Encoding.UTF8.GetString(bytes);

        if (!NzbDocument.TryParse(xml, out var payload))
            return new { status = false, error = "This nzb did not come from Smurfm3u" };

        // The document is the authority on what this grab covers: one entry, or a season's
        // worth in the order they should be transferred.
        return await QueueAsync(
            () => downloads.EnqueueItemsAsync(
                payload.ItemIds,
                string.IsNullOrWhiteSpace(request.NzbName) ? payload.ReleaseName : request.NzbName,
                request.Category, request.Priority, ct),
            ct);
    }

    private async Task<object> AddUrlAsync(SabRequest request, CancellationToken ct)
    {
        var url = request.Name ?? request.Value;
        if (string.IsNullOrWhiteSpace(url))
            return new { status = false, error = "No url supplied" };

        // The url is almost always our own t=get link, so read the id straight off it.
        if (TryReadIdFromUrl(url, out var downloadId))
        {
            return await QueueAsync(
                () => downloads.EnqueueAsync(
                    downloadId, request.Category, request.Priority, request.NzbName, ct),
                ct);
        }

        try
        {
            var client = httpClientFactory.CreateClient("playlist");
            var xml = await client.GetStringAsync(url, ct);

            if (NzbDocument.TryParse(xml, out var payload))
            {
                return await QueueAsync(
                    () => downloads.EnqueueItemsAsync(
                        payload.ItemIds,
                        string.IsNullOrWhiteSpace(request.NzbName) ? payload.ReleaseName : request.NzbName,
                        request.Category, request.Priority, ct),
                    ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch nzb from {Url}", url);
            return new { status = false, error = "Could not fetch the nzb" };
        }

        return new { status = false, error = "This nzb did not come from Smurfm3u" };
    }

    private async Task<object> QueueAsync(Func<Task<DownloadItem>> enqueue, CancellationToken ct)
    {
        try
        {
            var download = await enqueue();
            return new { status = true, nzo_ids = new[] { download.NzoId } };
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not queue that release");
            return new { status = false, error = ex.Message };
        }
    }

    private async Task<object> SetPausedAsync(bool paused, CancellationToken ct)
    {
        await downloads.SetAllPausedAsync(paused, ct);
        return new { status = true };
    }

    private async Task<object> ConfigAsync(SabRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.Name, "speedlimit", StringComparison.OrdinalIgnoreCase))
            return new { status = true };

        // Clients send KiB/s here, and 0 means unlimited.
        if (!int.TryParse(request.Value, out var kibps) || kibps < 0)
            return new { status = false, error = "Invalid speed limit" };

        await settingsService.UpdateAsync(s => s.ManualSpeedLimitKibps = kibps, ct);
        await speedLimits.RefreshAsync(ct);

        return new { status = true };
    }

    private async Task<object> FullStatusAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);

        return new
        {
            status = new
            {
                version = ReportedVersion,
                paused = speedLimits.Current.Paused,
                pause_int = "0",
                speedlimit = settings.ManualSpeedLimitKibps.ToString(),
                speedlimit_abs = (settings.ManualSpeedLimitKibps * 1024L).ToString(),
                have_warnings = "0",
                diskspace1 = DiskFreeGb(settings.CompletePath),
                diskspace2 = DiskFreeGb(settings.IncompletePath),
                diskspacetotal1 = DiskTotalGb(settings.CompletePath),
                diskspacetotal2 = DiskTotalGb(settings.IncompletePath),
                warnings = Array.Empty<object>()
            }
        };
    }

    private static string[] SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryReadIdFromUrl(string url, out string downloadId)
    {
        downloadId = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);

        if (!query.TryGetValue("id", out var raw)) return false;

        downloadId = raw.ToString();
        return downloadId.Length > 0;
    }

    private static string StatusName(DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => "Failed",
        _ => "Deleted"
    };

    private static string AverageAge(DateTimeOffset queuedAt)
    {
        var days = (int)(DateTimeOffset.UtcNow - queuedAt).TotalDays;
        return $"{Math.Max(0, days)}d";
    }

    private static string DiskFreeGb(string path) => DiskGb(path, total: false);

    private static string DiskTotalGb(string path) => DiskGb(path, total: true);

    /// <summary>
    /// Reported in GB as SAB does. The clients use it to refuse a grab when space is short,
    /// so an unreadable path reports a large number rather than blocking everything.
    /// </summary>
    private static string DiskGb(string path, bool total)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "1000.00";

            var drive = new DriveInfo(root);
            var bytes = total ? drive.TotalSize : drive.AvailableFreeSpace;

            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{bytes / 1024d / 1024d / 1024d:0.00}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "1000.00";
        }
    }
}
