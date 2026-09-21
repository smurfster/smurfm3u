using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

public sealed record RefreshResult(
    int SourceId, int TotalEntries, int VodEntries, int Added, int Deactivated, TimeSpan Elapsed);

/// <summary>
/// Ingests a playlist into the database: reads it, drops everything that is not on demand,
/// parses what is left into release metadata, and reconciles against the previous refresh.
/// </summary>
public class M3uRefreshService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider clock,
    ILogger<M3uRefreshService> logger)
{
    /// <summary>Rows written per SaveChanges; keeps memory flat on playlists with millions of lines.</summary>
    private const int BatchSize = 1000;

    /// <summary>Stops two refreshes of the same source from overlapping.</summary>
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public async Task<RefreshResult> RefreshAsync(int sourceId, CancellationToken ct = default)
    {
        await RefreshGate.WaitAsync(ct);
        try
        {
            return await RefreshCoreAsync(sourceId, ct);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private async Task<RefreshResult> RefreshCoreAsync(int sourceId, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var runStamp = clock.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var source = await db.Sources.FirstOrDefaultAsync(x => x.Id == sourceId, ct)
                     ?? throw new InvalidOperationException($"Source {sourceId} no longer exists.");

        source.LastRefreshStartedAt = runStamp;
        source.LastRefreshStatus = RefreshStatus.Running;
        source.LastRefreshError = null;
        await db.SaveChangesAsync(ct);

        var total = 0;
        var vod = 0;
        var added = 0;

        try
        {
            // ItemKey -> row id, so existing entries can be updated without loading them whole.
            var existing = await db.Items
                .AsNoTracking()
                .Where(x => x.SourceId == sourceId)
                .Select(x => new { x.ItemKey, x.Id })
                .ToDictionaryAsync(x => x.ItemKey, x => x.Id, ct);

            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var pending = 0;

            await using var stream = await OpenAsync(source, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            await foreach (var entry in M3uParser.ParseAsync(reader, ct))
            {
                total++;

                var verdict = VodClassifier.Classify(entry);
                if (!verdict.IsVod) continue;

                vod++;

                var key = ItemKeyFor(entry.Url);
                var parsed = ReleaseTitleParser.Parse(entry.DisplayName, verdict.Hint);

                if (existing.TryGetValue(key, out var id))
                {
                    var stub = Populate(new M3uItem { Id = id }, sourceId, key, entry, verdict, parsed, runStamp);
                    var tracked = db.Entry(stub);
                    tracked.State = EntityState.Modified;
                    // FirstSeenAt is only ever set on insert; the stub does not know the real value.
                    tracked.Property(x => x.FirstSeenAt).IsModified = false;
                }
                else
                {
                    var item = Populate(new M3uItem(), sourceId, key, entry, verdict, parsed, runStamp);
                    item.FirstSeenAt = runStamp;
                    db.Items.Add(item);
                    added++;
                }

                if (++pending >= BatchSize)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    pending = 0;
                }
            }

            if (pending > 0)
                await db.SaveChangesAsync(ct);

            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = true;

            // Anything this run did not touch has left the playlist. Rows are retired rather than
            // deleted so finished downloads still have something to point back at.
            var deactivated = await db.Items
                .Where(x => x.SourceId == sourceId && x.IsActive && x.LastSeenAt < runStamp)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

            var fresh = await db.Sources.FirstAsync(x => x.Id == sourceId, ct);
            fresh.LastRefreshCompletedAt = clock.GetUtcNow();
            fresh.LastRefreshStatus = RefreshStatus.Success;
            fresh.LastRefreshError = null;
            fresh.TotalEntries = total;
            fresh.VodEntries = vod;
            await db.SaveChangesAsync(ct);

            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogInformation(
                "Refreshed {Source}: {Vod} on-demand of {Total} entries, {Added} new, {Deactivated} retired, in {Elapsed}",
                fresh.Name, vod, total, added, deactivated, elapsed);

            return new RefreshResult(sourceId, total, vod, added, deactivated, elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Refresh of source {SourceId} failed", sourceId);

            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = true;

            var failed = await db.Sources.FirstAsync(x => x.Id == sourceId, CancellationToken.None);
            failed.LastRefreshCompletedAt = clock.GetUtcNow();
            failed.LastRefreshStatus = RefreshStatus.Failed;
            failed.LastRefreshError = Truncate(ex.Message, 2000);
            await db.SaveChangesAsync(CancellationToken.None);

            throw;
        }
    }

    private static M3uItem Populate(
        M3uItem item, int sourceId, string key, M3uEntry entry,
        VodVerdict verdict, ParsedTitle parsed, DateTimeOffset stamp)
    {
        item.SourceId = sourceId;
        item.ItemKey = key;
        item.RawTitle = Truncate(entry.DisplayName, 1000) ?? string.Empty;
        item.StreamUrl = Truncate(entry.Url, 2048) ?? string.Empty;
        item.GroupTitle = Truncate(entry.GroupTitle, 300);
        item.TvgId = Truncate(entry.TvgId, 200);
        item.TvgName = Truncate(entry.TvgName, 300);
        item.TvgLogo = Truncate(entry.TvgLogo, 2048);
        item.DurationSeconds = entry.DurationSeconds;
        item.Kind = parsed.Kind;
        item.Title = Truncate(parsed.Title, 500) ?? string.Empty;
        item.SearchTitle = Truncate(parsed.SearchTitle, 500) ?? string.Empty;
        item.Year = parsed.Year;
        item.Season = parsed.Season;
        item.Episode = parsed.Episode;
        item.EpisodeTitle = Truncate(parsed.EpisodeTitle, 500);
        item.Extension = verdict.Extension.Length > 0 ? verdict.Extension : "mp4";
        item.LastSeenAt = stamp;
        item.IsActive = true;
        return item;
    }

    private async Task<Stream> OpenAsync(M3uSource source, CancellationToken ct)
    {
        if (source.Kind == M3uSourceKind.Local)
        {
            if (!File.Exists(source.Location))
                throw new FileNotFoundException($"Playlist not found at {source.Location}.", source.Location);

            return File.OpenRead(source.Location);
        }

        var client = httpClientFactory.CreateClient("playlist");
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
        ApplyHeaders(request, source.Headers);

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Applies the source's custom headers, written one "Name: value" per line.</summary>
    public static void ApplyHeaders(HttpRequestMessage request, string? headers)
    {
        if (string.IsNullOrWhiteSpace(headers)) return;

        var lines = headers.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0) continue;

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Identity of an entry within its source. The stream URL is the only stable handle providers
    /// give us; if one rotates, the entry reads as new and the old one retires on the same pass.
    /// </summary>
    public static string ItemKeyFor(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
