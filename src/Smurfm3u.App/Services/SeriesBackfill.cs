using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Core.Xtream;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Fetches a panel series' episodes the first time something searches for it.
/// <para>
/// A panel lists its series in one request and each series' episodes in one request of their
/// own. Reading every episode list in advance is tens of thousands of requests for a catalogue
/// almost none of which anyone will ever ask about. This asks for the few that are.
/// </para>
/// </summary>
public class SeriesBackfill(
    IDbContextFactory<AppDbContext> dbFactory,
    XtreamClient xtream,
    TimeProvider clock,
    ILogger<SeriesBackfill> logger)
{
    /// <summary>
    /// How many series one query may pull in. Each is a request the searcher waits for, and a
    /// vague query can match hundreds; the *arrs search by title, so the few best will do.
    /// </summary>
    private const int MostPerSearch = 3;

    /// <summary>
    /// Makes sure the episodes of any series matching these words are on hand, fetching them
    /// if they are missing or if the panel has changed the series since they were fetched.
    /// </summary>
    /// <returns>How many episodes were stored.</returns>
    public async Task<int> EnsureEpisodesAsync(IReadOnlyList<string> tokens, CancellationToken ct = default)
    {
        if (tokens.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Series.AsNoTracking().Where(x => x.IsActive);

        foreach (var token in tokens)
        {
            var needle = token;
            query = query.Where(x => EF.Functions.Like(x.SearchTitle, $"%{needle}%"));
        }

        // The ones that would answer this query but have nothing to answer it with.
        var wanted = await query
            .Where(x => x.EpisodesFetchedFor == null || x.EpisodesFetchedFor != x.LastModified)
            .OrderBy(x => x.SearchTitle.Length)
            .ThenBy(x => x.Id)
            .Take(MostPerSearch)
            .ToListAsync(ct);

        if (wanted.Count == 0) return 0;

        var stored = 0;

        foreach (var series in wanted)
        {
            ct.ThrowIfCancellationRequested();

            stored += await FetchAsync(db, series, ct);
        }

        return stored;
    }

    private async Task<int> FetchAsync(AppDbContext db, M3uSeries series, CancellationToken ct)
    {
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == series.SourceId, ct);

        if (source is null || source.Kind != M3uSourceKind.Xtream) return 0;

        if (!XtreamCredentials.TryCreate(source.Location, source.Username, source.Password, out var creds, out var error))
        {
            logger.LogWarning("Cannot fetch episodes for {Series}: {Reason}", series.Title, error);
            return 0;
        }

        var stamp = clock.GetUtcNow();

        List<IngestCandidate> episodes;

        try
        {
            episodes = (await xtream.EpisodesAsync(
                source,
                creds,
                new XtreamSeries { SeriesId = series.SeriesId, Name = series.Name, LastModified = series.LastModified },
                series.GroupTitle,
                ct)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A search is waiting on this. Answering from what is already stored beats failing.
            logger.LogWarning(ex, "Could not fetch episodes for {Series} ({Id})", series.Title, series.SeriesId);
            return 0;
        }

        var keys = episodes.Select(x => M3uRefreshService.ItemKeyFor(x.Entry.Url)).ToList();

        var existing = await db.Items
            .AsNoTracking()
            .Where(x => x.SourceId == source.Id && keys.Contains(x.ItemKey))
            .Select(x => new { x.ItemKey, x.Id })
            .ToDictionaryAsync(x => x.ItemKey, x => x.Id, ct);

        db.ChangeTracker.AutoDetectChangesEnabled = false;

        for (var i = 0; i < episodes.Count; i++)
        {
            var candidate = episodes[i];
            var key = keys[i];
            var parsed = candidate.Parsed
                         ?? ReleaseTitleParser.Parse(candidate.Entry.DisplayName, candidate.Verdict.Hint);

            if (existing.TryGetValue(key, out var id))
            {
                var stub = M3uRefreshService.Populate(new M3uItem { Id = id }, source.Id, key, candidate, parsed, stamp);
                var tracked = db.Entry(stub);
                tracked.State = EntityState.Modified;
                tracked.Property(x => x.FirstSeenAt).IsModified = false;
            }
            else
            {
                var item = M3uRefreshService.Populate(new M3uItem(), source.Id, key, candidate, parsed, stamp);
                item.FirstSeenAt = stamp;
                db.Items.Add(item);
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        db.ChangeTracker.AutoDetectChangesEnabled = true;

        // Recorded against the stamp they were fetched for, so the next refresh moving it is
        // what marks them stale rather than a clock.
        await db.Series
            .Where(x => x.Id == series.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.EpisodesFetchedFor, series.LastModified)
                .SetProperty(x => x.EpisodesFetchedAt, stamp), ct);

        logger.LogInformation("Fetched {Count} episodes of {Series} because a search asked for it",
            episodes.Count, series.Title);

        return episodes.Count;
    }
}
