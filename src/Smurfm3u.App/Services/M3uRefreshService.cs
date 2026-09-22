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

    /// <summary>
    /// How long a panel's own last-changed stamps are trusted before one run reads everything
    /// regardless. Short enough that a panel which neglects them cannot hide new episodes for
    /// long, long enough that the expensive walk stays rare.
    /// </summary>
    private static readonly TimeSpan FullWalkEvery = TimeSpan.FromDays(7);

    /// <summary>Series ids are chunked into IN clauses rather than sent as one enormous list.</summary>
    private const int KeepAliveChunk = 2000;

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

        // Decided before anything is read, because the answer is what makes the read full.
        var wasFullWalk = IsFullWalkDue(source);

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

            // A series left alone because the panel says it has not changed was never read, so
            // none of its episodes were touched above. They are still there; marking them seen
            // is what stops the reconcile below retiring an entire series for not being asked
            // about. This has to happen before the retire, not after.
            var kept = await KeepUnchangedAsync(db, sourceId, runStamp, ct);

            // Anything this run did not touch has left the playlist. Rows are retired rather than
            // deleted so finished downloads still have something to point back at.
            var deactivated = await db.Items
                .Where(x => x.SourceId == sourceId && x.IsActive && x.LastSeenAt < runStamp)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

            var fresh = await db.Sources.FirstAsync(x => x.Id == sourceId, ct);
            fresh.LastRefreshCompletedAt = clock.GetUtcNow();
            fresh.LastRefreshStatus = RefreshStatus.Success;
            fresh.LastRefreshError = null;
            fresh.TotalEntries = total + (int)kept;
            fresh.VodEntries = vod + (int)kept;

            // Only a run that actually read every series may claim one, or the weekly full walk
            // would keep pushing itself back and never happen.
            if (wasFullWalk) fresh.LastFullRefreshAt = runStamp;

            await db.SaveChangesAsync(ct);

            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.LogInformation(
                "Refreshed {Source}: {Vod} on-demand of {Total} entries, {Added} new, {Kept} left alone, {Deactivated} retired, in {Elapsed}",
                fresh.Name, vod, total, added, kept, deactivated, elapsed);

            return new RefreshResult(sourceId, total + (int)kept, vod + (int)kept, added, deactivated, elapsed);
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

    private static M3uItem Populate(
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
        item.SeriesLastModified = candidate.SeriesLastModified;
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

            var known = await KnownSeriesAsync(source, ct);

            await foreach (var candidate in xtream.EnumerateAsync(source, creds, known, ct))
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
    /// The playlist, and how many bytes of it to expect. The length is null when the provider
    /// streams without declaring one, which is the case with no denominator to show.
    /// </summary>
    /// <summary>
    /// Marks the episodes of every series that was left alone as seen on this run, so the
    /// reconcile treats them as present rather than gone. Returns how many rows that was.
    /// </summary>
    private async Task<long> KeepUnchangedAsync(
        AppDbContext db, int sourceId, DateTimeOffset runStamp, CancellationToken ct)
    {
        var skipped = xtream.UnchangedSeries;
        if (skipped.Count == 0) return 0;

        long kept = 0;

        foreach (var chunk in skipped.Chunk(KeepAliveChunk))
        {
            kept += await db.Items
                .Where(x => x.SourceId == sourceId && x.SeriesId != null && chunk.Contains(x.SeriesId))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.LastSeenAt, runStamp).SetProperty(x => x.IsActive, true), ct);
        }

        logger.LogInformation("{Source}: kept {Kept} episodes of {Series} unchanged series",
            sourceId, kept, skipped.Count);

        return kept;
    }

    /// <summary>
    /// What the panel last said each series changed at, taken from the episodes already stored.
    /// Empty on a full walk, which is what makes a full walk full.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long>> KnownSeriesAsync(M3uSource source, CancellationToken ct)
    {
        if (IsFullWalkDue(source))
        {
            logger.LogInformation(
                "{Source}: reading every series this run{Because}",
                source.Name,
                source.LastFullRefreshAt is null
                    ? ", because none has been read in full yet"
                    : $", because the last full read was {source.LastFullRefreshAt:yyyy-MM-dd}");

            return new Dictionary<string, long>();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var known = await db.Items
            .AsNoTracking()
            .Where(x => x.SourceId == source.Id && x.SeriesId != null && x.SeriesLastModified != null)
            .GroupBy(x => x.SeriesId!)
            .Select(g => new { SeriesId = g.Key, Stamp = g.Max(x => x.SeriesLastModified!.Value) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Stamp, ct);

        logger.LogInformation("{Source}: {Known} series already read, checking which have changed",
            source.Name, known.Count);

        return known;
    }

    /// <summary>
    /// Whether to ignore the panel's own last-changed stamps and read everything. A panel that
    /// does not keep them current would otherwise hide new episodes for as long as it kept
    /// getting away with it, so trusting them is time-limited rather than permanent.
    /// </summary>
    private bool IsFullWalkDue(M3uSource source) =>
        source.Kind == M3uSourceKind.Xtream
        && (source.LastFullRefreshAt is not { } last || clock.GetUtcNow() - last >= FullWalkEvery);

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
