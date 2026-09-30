using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Diagnostics;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Core.Xtream;
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
    XtreamClient xtream,
    SeriesBackfill backfill,
    RefreshProgress progress,
    TimeProvider clock,
    NotificationService notifications,
    ILogger<M3uRefreshService> logger)
{
    /// <summary>Rows written per SaveChanges; keeps memory flat on playlists with millions of lines.</summary>
    private const int BatchSize = 1000;

    /// <summary>How many entries between progress lines. Often enough to show movement on a
    /// long playlist, rare enough that it does not become the log.</summary>
    private const int ProgressInterval = 25000;

    /// <summary>Entries between updates of the live progress the page watches.</summary>
    private const int ProgressSamples = 500;

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

        logger.LogInformation("Refreshing {Source}: reading {Kind} {Location}",
            source.Name, Describe(source.Kind), SafeUrl.Redact(source.Location));

        try
        {
            // ItemKey -> row id, so existing entries can be updated without loading them whole.
            var existing = await db.Items
                .AsNoTracking()
                .Where(x => x.SourceId == sourceId)
                .Select(x => new { x.ItemKey, x.Id })
                .ToDictionaryAsync(x => x.ItemKey, x => x.Id, ct);

            logger.LogInformation("{Source}: reconciling against {Known} entries already known",
                source.Name, existing.Count);

            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var pending = 0;
            var nextReport = ProgressInterval;

            await foreach (var candidate in ReadAsync(source, ct))
            {
                total++;

                if (!candidate.Verdict.IsVod) continue;

                vod++;

                var entry = candidate.Entry;
                var key = ItemKeyFor(entry.Url);

                // A panel states the season and episode; a playlist leaves them to be read
                // back out of the display name, which is the best that can be done there.
                var parsed = candidate.Parsed
                             ?? ReleaseTitleParser.Parse(entry.DisplayName, candidate.Verdict.Hint);

                if (existing.TryGetValue(key, out var id))
                {
                    var stub = Populate(new M3uItem { Id = id }, sourceId, key, candidate, parsed, runStamp);
                    var tracked = db.Entry(stub);
                    tracked.State = EntityState.Modified;
                    // FirstSeenAt is only ever set on insert; the stub does not know the real value.
                    tracked.Property(x => x.FirstSeenAt).IsModified = false;
                }
                else
                {
                    var item = Populate(new M3uItem(), sourceId, key, candidate, parsed, runStamp);
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

                // A large playlist runs for minutes, and silence for minutes reads as a hang.
                if (total >= nextReport)
                {
                    logger.LogInformation("{Source}: {Total} entries read, {Vod} on demand, {Added} new so far",
                        source.Name, total, vod, added);

                    nextReport += ProgressInterval;
                }
            }

            if (pending > 0)
                await db.SaveChangesAsync(ct);

            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = true;

            // The series list, which is one request whatever its size. Their episodes are not
            // fetched here at all; a search asks for the ones it needs.
            var series = await SyncSeriesAsync(db, source, runStamp, ct);

            // Except for the ones nobody has asked about in a week. Everything else here waits
            // to be asked; this is the part that does not, so a series can go stale but only
            // for so long.
            var rechecked = source.Kind == M3uSourceKind.Xtream
                ? await backfill.RecheckStaleAsync(sourceId, ct)
                : 0;

            // Anything this run did not touch has left the playlist. Rows are retired rather than
            // deleted so finished downloads still have something to point back at.
            //
            // Episodes are exempt: they arrive when a search asks for them, not from this loop,
            // so judging them by whether this run touched them would retire every one of them
            // every time. They are retired with their series instead, in SyncSeriesAsync.
            var deactivated = await db.Items
                .Where(x => x.SourceId == sourceId && x.IsActive && x.LastSeenAt < runStamp
                            && x.SeriesId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

            var finishedAt = clock.GetUtcNow();

            var fresh = await db.Sources.FirstAsync(x => x.Id == sourceId, ct);
            fresh.LastRefreshCompletedAt = finishedAt;
            fresh.LastSuccessfulRefreshAt = finishedAt;
            fresh.LastRefreshStatus = RefreshStatus.Success;
            fresh.LastRefreshError = null;
            fresh.TotalEntries = total;
            fresh.VodEntries = vod;

            await db.SaveChangesAsync(ct);

            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogInformation(
                "Refreshed {Source}: {Vod} on-demand of {Total} entries, {Added} new, {Series} series listed, {Rechecked} re-read, {Deactivated} retired, in {Elapsed}",
                fresh.Name, vod, total, added, series, rechecked, deactivated, elapsed);

            return new RefreshResult(sourceId, total, vod, added, deactivated, elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Refresh of {Source} failed after {Total} entries: {Reason}",
                source.Name, total, ex.Message);

            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = true;

            var failed = await db.Sources.FirstAsync(x => x.Id == sourceId, CancellationToken.None);
            failed.LastRefreshCompletedAt = clock.GetUtcNow();
            failed.LastRefreshStatus = RefreshStatus.Failed;
            failed.LastRefreshError = Truncate(ex.Message, 2000);
            await db.SaveChangesAsync(CancellationToken.None);

            notifications.Notify(Core.Options.NotificationEvent.RefreshFailed, failed.Name,
            [
                new("Location", SafeUrl.Redact(failed.Location)),
                new("Reason", ex.Message)
            ]);

            throw;
        }
        finally
        {
            // However it ended. Left behind, the page would show a bar that never moves again.
            progress.Finish(sourceId);
        }
    }

    /// <summary>Shared with the lazy episode fetch, which stores the same shape of row.</summary>
    internal static M3uItem Populate(
        M3uItem item, int sourceId, string key, IngestCandidate candidate,
        ParsedTitle parsed, DateTimeOffset stamp)
    {
        var entry = candidate.Entry;
        var verdict = candidate.Verdict;

        item.SourceId = sourceId;
        item.ItemKey = key;
        item.RawTitle = Truncate(entry.DisplayName, 1000) ?? string.Empty;
        item.StreamUrl = Truncate(entry.Url, 2048) ?? string.Empty;
        item.GroupTitle = Truncate(entry.GroupTitle, 300);
        item.TvgId = Truncate(entry.TvgId, 200);
        item.TvgName = Truncate(entry.TvgName, 300);
        item.TvgLogo = Truncate(entry.TvgLogo, 2048);
        item.DurationSeconds = entry.DurationSeconds;
        item.SeriesId = Truncate(candidate.SeriesId, 64);
        item.Kind = parsed.Kind;
        item.Title = Truncate(parsed.Title, 500) ?? string.Empty;
        item.SearchTitle = Truncate(parsed.SearchTitle, 500) ?? string.Empty;
        item.Year = parsed.Year;
        item.Season = parsed.Season;
        item.Episode = parsed.Episode;
        item.EpisodeTitle = Truncate(parsed.EpisodeTitle, 500);
        item.AirDate = parsed.AirDate;
        item.Extension = verdict.Extension.Length > 0 ? verdict.Extension : "mp4";
        item.LastSeenAt = stamp;
        item.IsActive = true;
        return item;
    }

    /// <summary>
    /// Everything the source offers, however it has to be asked. A playlist is read as lines
    /// and classified, because nothing in the M3U format says what is on demand; a panel is
    /// asked for its on-demand content directly, so everything it returns already counts.
    /// </summary>
    private async IAsyncEnumerable<IngestCandidate> ReadAsync(
        M3uSource source, [EnumeratorCancellation] CancellationToken ct)
    {
        if (source.Kind == M3uSourceKind.Xtream)
        {
            if (!XtreamCredentials.TryCreate(source.Location, source.Username, source.Password, out var creds, out var error))
                throw new InvalidOperationException(error);

            await foreach (var candidate in xtream.EnumerateFilmsAsync(source, creds, ct))
                yield return candidate;

            yield break;
        }

        var (raw, declared) = await OpenAsync(source, ct);

        // Counted through a wrapper because a playlist is parsed as it arrives: bytes consumed
        // is the only honest measure of how far in it is, and the only one with a denominator.
        await using var stream = new CountingStream(raw);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        progress.Begin(source.Id, "playlist", declared);

        var seen = 0;

        await foreach (var entry in M3uParser.ParseAsync(reader, ct))
        {
            // Often enough to move smoothly, rarely enough to stay off the parse's back.
            if (++seen % ProgressSamples == 0) progress.Report(source.Id, stream.BytesRead);

            yield return new IngestCandidate(entry, VodClassifier.Classify(entry));
        }
    }

    /// <summary>
    /// Brings the series list up to date, and does not touch a single episode list doing it.
    /// One request however large the catalogue, against one request per series for the
    /// episodes - which is why those are left until something asks for them.
    /// </summary>
    /// <returns>How many series the panel currently offers.</returns>
    private async Task<int> SyncSeriesAsync(
        AppDbContext db, M3uSource source, DateTimeOffset runStamp, CancellationToken ct)
    {
        if (source.Kind != M3uSourceKind.Xtream || !source.IncludeSeries) return 0;

        if (!XtreamCredentials.TryCreate(source.Location, source.Username, source.Password, out var creds, out var error))
            throw new InvalidOperationException(error);

        progress.Begin(source.Id, "series list", null);

        var (series, categories) = await xtream.ListSeriesAsync(source, creds, ct);

        logger.LogInformation("{Source}: panel lists {Count} series; episodes are fetched when searched for",
            source.Name, series.Count);

        // Id and stamp only: thirty thousand tracked entities would cost more than the request.
        var existing = await db.Series
            .AsNoTracking()
            .Where(x => x.SourceId == source.Id)
            .Select(x => new { x.SeriesId, x.Id, x.EpisodesFetchedFor, x.EpisodesFetchedAt })
            .ToDictionaryAsync(x => x.SeriesId, ct);

        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var seen = 0;
        var pending = 0;

        foreach (var entry in series)
        {
            if (string.IsNullOrWhiteSpace(entry.SeriesId) || string.IsNullOrWhiteSpace(entry.Name)) continue;

            seen++;

            var parsed = ReleaseTitleParser.Parse(entry.Name, MediaKind.Series);

            var row = new M3uSeries
            {
                SourceId = source.Id,
                SeriesId = entry.SeriesId,
                Name = Truncate(entry.Name, 500) ?? string.Empty,
                Title = Truncate(parsed.Title, 500) ?? string.Empty,
                SearchTitle = Truncate(parsed.SearchTitle, 500) ?? string.Empty,
                Year = parsed.Year,
                GroupTitle = Truncate(Lookup(categories, entry.CategoryId), 300),
                Cover = Truncate(entry.Cover, 2048),
                LastModified = entry.LastModified,
                LastSeenAt = runStamp,
                IsActive = true
            };

            if (existing.TryGetValue(entry.SeriesId, out var known))
            {
                // Carried over rather than reset: whatever episodes are on hand were fetched
                // for the stamp they were fetched for, and a changed stamp is what makes them
                // stale. Overwriting this would refetch every series that anyone had opened.
                row.Id = known.Id;
                row.EpisodesFetchedFor = known.EpisodesFetchedFor;
                row.EpisodesFetchedAt = known.EpisodesFetchedAt;

                db.Entry(row).State = EntityState.Modified;
            }
            else
            {
                db.Series.Add(row);
            }

            if (++pending >= BatchSize)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0) await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        db.ChangeTracker.AutoDetectChangesEnabled = true;

        // A series that has left the panel takes its episodes with it. They are retired rather
        // than deleted, for the same reason every other entry is: a finished download still
        // points at one.
        var gone = await db.Series
            .Where(x => x.SourceId == source.Id && x.IsActive && x.LastSeenAt < runStamp)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

        if (gone > 0)
        {
            var orphaned = await db.Items
                .Where(x => x.SourceId == source.Id && x.IsActive && x.SeriesId != null
                            && !db.Series.Any(s => s.SourceId == source.Id && s.SeriesId == x.SeriesId && s.IsActive))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

            logger.LogInformation("{Source}: {Gone} series have left the panel, retiring {Orphaned} of their episodes",
                source.Name, gone, orphaned);
        }

        return seen;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> categories, string? id) =>
        id is not null && categories.TryGetValue(id, out var name) ? name : null;

    /// <summary>
    /// The playlist, and how many bytes of it to expect. The length is null when the provider
    /// streams without declaring one, which is the case with no denominator to show.
    /// </summary>

    private async Task<(Stream Stream, long? Length)> OpenAsync(M3uSource source, CancellationToken ct)
    {
        if (source.Kind == M3uSourceKind.Local)
        {
            if (!File.Exists(source.Location))
                throw new FileNotFoundException($"Playlist not found at {source.Location}.", source.Location);

            var file = new FileInfo(source.Location);
            logger.LogInformation("{Source}: opening {Path}, {Size}",
                source.Name, source.Location, Core.Options.NotificationComposer.FormatBytes(file.Length));

            return (File.OpenRead(source.Location), file.Length);
        }

        var client = httpClientFactory.CreateClient("playlist");
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Location);
        ApplyHeaders(request, source.Headers);

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // The size is the useful part: it says whether the provider sent a playlist or a
        // one-line error page, long before the parse finds out.
        logger.LogInformation("{Source}: provider answered HTTP {Status}, {Size}",
            source.Name,
            (int)response.StatusCode,
            response.Content.Headers.ContentLength is { } length
                ? Core.Options.NotificationComposer.FormatBytes(length)
                : "length not declared");

        return (await response.Content.ReadAsStreamAsync(ct), response.Content.Headers.ContentLength);
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

    /// <summary>How a source kind reads in a log line.</summary>
    private static string Describe(M3uSourceKind kind) => kind switch
    {
        M3uSourceKind.Local => "local file",
        M3uSourceKind.Xtream => "Xtream panel",
        _ => "remote playlist"
    };

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
