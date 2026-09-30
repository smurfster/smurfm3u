using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>What one playlist currently holds.</summary>
public sealed record CacheSummary(
    int SourceId,
    string Name,
    M3uSourceKind Kind,
    bool Enabled,
    int Films,
    int Episodes,
    int SeriesListed,
    int SeriesFetched);

/// <summary>
/// One show's worth of cached episodes. <paramref name="Key"/> is opaque and only has to tell
/// two listed rows apart; the page hands the whole record back when it clears.
/// </summary>
public sealed record CachedSeries(
    string Key,
    int SourceId,
    string? SeriesId,
    string Title,
    string SearchTitle,
    int? Year,
    int Seasons,
    int Episodes,
    DateTimeOffset? FetchedAt);

public sealed record CachedSeason(int? Season, int Episodes);

/// <summary>One cached entry, film or episode, as the browser shows it.</summary>
public sealed record CachedEntry(
    long Id,
    string Title,
    int? Year,
    int? Season,
    int? Episode,
    string? EpisodeTitle,
    string? GroupTitle,
    bool IsActive,
    DateTimeOffset FirstSeenAt);

/// <summary>What a clear actually removed.</summary>
public sealed record CacheCleared(int Entries, int SeriesReset)
{
    public static readonly CacheCleared Nothing = new(0, 0);

    public CacheCleared Plus(CacheCleared other) =>
        new(Entries + other.Entries, SeriesReset + other.SeriesReset);
}

/// <summary>
/// Reads and clears what the playlists have left in the database.
/// <para>
/// Clearing is not the same as forgetting. Films and the series list come back on the next
/// refresh because they arrive in bulk; a panel's episodes come back the next time a search
/// names their series, which is the only time they were ever fetched. So the series index
/// keeps its rows and loses only its fetch stamps, which is what makes a series look unread
/// again rather than unknown.
/// </para>
/// </summary>
public class CacheBrowserService(IDbContextFactory<AppDbContext> dbFactory, ILogger<CacheBrowserService> logger)
{
    /// <summary>Every playlist and what it is holding.</summary>
    public async Task<IReadOnlyList<CacheSummary>> SummariesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sources = await db.Sources
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Kind, x.Enabled })
            .ToListAsync(ct);

        // Two grouped queries rather than four per playlist, so the page costs the same
        // whether there is one playlist or twenty.
        var entries = await db.Items
            .AsNoTracking()
            .GroupBy(x => new { x.SourceId, x.Kind })
            .Select(g => new { g.Key.SourceId, g.Key.Kind, Count = g.Count() })
            .ToListAsync(ct);

        var series = await db.Series
            .AsNoTracking()
            .GroupBy(x => x.SourceId)
            .Select(g => new
            {
                SourceId = g.Key,
                Listed = g.Count(),
                Fetched = g.Count(x => x.EpisodesFetchedAt != null)
            })
            .ToListAsync(ct);

        return sources.Select(source =>
        {
            var films = entries.FirstOrDefault(x => x.SourceId == source.Id && x.Kind == MediaKind.Movie)?.Count ?? 0;
            var episodes = entries.FirstOrDefault(x => x.SourceId == source.Id && x.Kind == MediaKind.Series)?.Count ?? 0;
            var index = series.FirstOrDefault(x => x.SourceId == source.Id);

            return new CacheSummary(
                source.Id, source.Name, source.Kind, source.Enabled,
                films, episodes, index?.Listed ?? 0, index?.Fetched ?? 0);
        }).ToList();
    }

    /// <summary>
    /// The shows this playlist holds episodes for.
    /// <para>
    /// Grouped by the panel's series id where there is one and by title and year where there
    /// is not, so an m3u - which has no series ids at all - groups just as well as a panel.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<CachedSeries>> SeriesAsync(
        int sourceId, string? filter, int offset, int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var grouped = Grouped(Episodes(db, sourceId, filter));

        var rows = await grouped
            .OrderBy(x => x.Title)
            .ThenBy(x => x.Year)
            .Skip(Math.Max(0, offset))
            .Take(limit)
            .ToListAsync(ct);

        // When the episodes came from a panel, the index knows when they were read. Looked up
        // for the page in hand only, so a playlist of thirty thousand series costs one small query.
        var ids = rows.Where(x => x.SeriesId != null).Select(x => x.SeriesId!).ToList();

        var fetched = ids.Count == 0
            ? []
            : await db.Series
                .AsNoTracking()
                .Where(x => x.SourceId == sourceId && ids.Contains(x.SeriesId))
                .Select(x => new { x.SeriesId, x.EpisodesFetchedAt })
                .ToDictionaryAsync(x => x.SeriesId, x => x.EpisodesFetchedAt, ct);

        return rows.Select(x => new CachedSeries(
            Key: Key(x.SeriesId, x.SearchTitle, x.Year),
            SourceId: sourceId,
            SeriesId: x.SeriesId,
            Title: string.IsNullOrWhiteSpace(x.Title) ? x.SearchTitle : x.Title,
            SearchTitle: x.SearchTitle,
            Year: x.Year,
            Seasons: x.Seasons,
            Episodes: x.Episodes,
            FetchedAt: x.SeriesId is { } id && fetched.TryGetValue(id, out var at) ? at : null)).ToList();
    }

    /// <summary>
    /// How many shows match, for the pager.
    /// <para>
    /// Separate from reading a page because counting them means aggregating every episode row
    /// in the playlist - about a second on a catalogue of 845,000, against seven milliseconds
    /// once a filter narrows it. Paging does not change the answer, so the page asks for it
    /// when the playlist or the filter changes and not on every page turn.
    /// </para>
    /// </summary>
    public async Task<int> CountSeriesAsync(int sourceId, string? filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await Grouped(Episodes(db, sourceId, filter)).CountAsync(ct);
    }

    /// <summary>The seasons held for one show, with how many episodes are in each.</summary>
    public async Task<IReadOnlyList<CachedSeason>> SeasonsAsync(CachedSeries series, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Ordered and projected in two steps: sorting by a property of a record built in the
        // projection is not something the provider can turn into SQL.
        var rows = await Scope(db, series)
            .GroupBy(x => x.Season)
            .Select(g => new { Season = g.Key, Episodes = g.Count() })
            .OrderBy(x => x.Season)
            .ToListAsync(ct);

        return rows.Select(x => new CachedSeason(x.Season, x.Episodes)).ToList();
    }

    /// <summary>The episodes held for one season of one show.</summary>
    public async Task<IReadOnlyList<CachedEntry>> EpisodesAsync(
        CachedSeries series, int? season, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = Scope(db, series);
        query = season is { } number ? query.Where(x => x.Season == number) : query.Where(x => x.Season == null);

        return await query
            .AsNoTracking()
            .OrderBy(x => x.Episode)
            .ThenBy(x => x.Id)
            .Select(x => new CachedEntry(
                x.Id, x.Title, x.Year, x.Season, x.Episode, x.EpisodeTitle, x.GroupTitle, x.IsActive, x.FirstSeenAt))
            .ToListAsync(ct);
    }

    /// <summary>The films this playlist holds, paged and filtered by title.</summary>
    public async Task<IReadOnlyList<CachedEntry>> FilmsAsync(
        int sourceId, string? filter, int offset, int limit, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = ApplyFilter(
            db.Items.AsNoTracking().Where(x => x.SourceId == sourceId && x.Kind == MediaKind.Movie),
            filter);

        return await query
            .OrderBy(x => x.Title)
            .ThenBy(x => x.Year)
            .ThenBy(x => x.Id)
            .Skip(Math.Max(0, offset))
            .Take(limit)
            .Select(x => new CachedEntry(
                x.Id, x.Title, x.Year, x.Season, x.Episode, x.EpisodeTitle, x.GroupTitle, x.IsActive, x.FirstSeenAt))
            .ToListAsync(ct);
    }

    /// <summary>How many films match, for the pager. Cheap, unlike the series count.</summary>
    public async Task<int> CountFilmsAsync(int sourceId, string? filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await ApplyFilter(
                db.Items.AsNoTracking().Where(x => x.SourceId == sourceId && x.Kind == MediaKind.Movie),
                filter)
            .CountAsync(ct);
    }

    /// <summary>
    /// Every film id matching the current filter, so "select all" can mean more than the page
    /// in view. Capped, because a playlist of 143,000 films is not a selection anyone made
    /// deliberately - clearing the whole playlist is the button for that.
    /// </summary>
    public async Task<IReadOnlyList<long>> FilmIdsAsync(
        int sourceId, string? filter, int cap, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await ApplyFilter(
                db.Items.AsNoTracking().Where(x => x.SourceId == sourceId && x.Kind == MediaKind.Movie),
                filter)
            .OrderBy(x => x.Title)
            .ThenBy(x => x.Id)
            .Take(cap)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }

    /// <summary>Everything this playlist holds. The series index keeps its rows and loses its stamps.</summary>
    public async Task<CacheCleared> ClearSourceAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entries = await db.Items.Where(x => x.SourceId == sourceId).ExecuteDeleteAsync(ct);
        var reset = await ResetStampsAsync(db, db.Series.Where(x => x.SourceId == sourceId), ct);

        logger.LogWarning("Cleared the cache for playlist {SourceId}: {Entries} entries, {Reset} series marked unread",
            sourceId, entries, reset);

        return new CacheCleared(entries, reset);
    }

    /// <summary>Whole shows, in full.</summary>
    public async Task<CacheCleared> ClearSeriesAsync(
        IReadOnlyCollection<CachedSeries> series, CancellationToken ct = default)
    {
        if (series.Count == 0) return CacheCleared.Nothing;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cleared = CacheCleared.Nothing;

        foreach (var show in series)
        {
            ct.ThrowIfCancellationRequested();

            var entries = await Scope(db, show).ExecuteDeleteAsync(ct);

            var reset = show.SeriesId is { } id
                ? await ResetStampsAsync(
                    db, db.Series.Where(x => x.SourceId == show.SourceId && x.SeriesId == id), ct)
                : 0;

            cleared = cleared.Plus(new CacheCleared(entries, reset));
        }

        logger.LogWarning("Cleared {Count} series from the cache: {Entries} episodes",
            series.Count, cleared.Entries);

        return cleared;
    }

    /// <summary>Whole seasons of a show, leaving its other seasons alone.</summary>
    public async Task<CacheCleared> ClearSeasonsAsync(
        IReadOnlyCollection<(CachedSeries Series, int? Season)> seasons, CancellationToken ct = default)
    {
        if (seasons.Count == 0) return CacheCleared.Nothing;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cleared = CacheCleared.Nothing;

        foreach (var (show, season) in seasons)
        {
            ct.ThrowIfCancellationRequested();

            var query = Scope(db, show);
            query = season is { } number ? query.Where(x => x.Season == number) : query.Where(x => x.Season == null);

            cleared = cleared.Plus(new CacheCleared(await query.ExecuteDeleteAsync(ct), 0));
        }

        // Marked unread as well: a show missing a season only gets it back if something reads
        // the show again, and the stamps are what decide whether anything bothers.
        var reset = await ResetStampsAsync(db, Indexed(db, seasons.Select(x => x.Series)), ct);

        cleared = cleared.Plus(new CacheCleared(0, reset));

        logger.LogWarning("Cleared {Count} season(s) from the cache: {Entries} episodes",
            seasons.Count, cleared.Entries);

        return cleared;
    }

    /// <summary>Individual entries, films or episodes, by id.</summary>
    public async Task<CacheCleared> ClearEntriesAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return CacheCleared.Nothing;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The series an episode belonged to has to be read again for it to come back, so the
        // stamps are cleared first - while the rows are still there to say which series those
        // were. Done in one statement per playlist rather than one per episode.
        var affected = await db.Items
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.SeriesId != null)
            .Select(x => new { x.SourceId, x.SeriesId })
            .Distinct()
            .ToListAsync(ct);

        var reset = 0;

        foreach (var group in affected.GroupBy(x => x.SourceId))
        {
            var sourceId = group.Key;
            var seriesIds = group.Select(x => x.SeriesId!).ToList();

            reset += await ResetStampsAsync(
                db, db.Series.Where(x => x.SourceId == sourceId && seriesIds.Contains(x.SeriesId)), ct);
        }

        var entries = await db.Items.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);

        logger.LogWarning("Cleared {Entries} individual entries from the cache", entries);

        return new CacheCleared(entries, reset);
    }

    /// <summary>How much disk the whole database is taking, in bytes.</summary>
    public async Task<long> DatabaseSizeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Database
            .SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"")
            .SingleAsync(ct);
    }

    /// <summary>
    /// Hands the space clearing freed back to the disk.
    /// <para>
    /// A delete in PostgreSQL only marks rows dead. Autovacuum later lets the table reuse that
    /// space, but the files never shrink, so clearing alone leaves the database exactly as big
    /// as it was. <c>VACUUM FULL</c> rewrites the tables and their indexes without the dead
    /// rows, which is what actually gives it back. It locks the tables while it runs, so
    /// searches and refreshes wait for it, and it needs room for the new copy while the old
    /// one still exists - which is why it is a button rather than something every clear does.
    /// </para>
    /// </summary>
    public async Task<(long Before, long After)> CompactAsync(CancellationToken ct = default)
    {
        var before = await DatabaseSizeAsync(ct);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Rewriting a catalogue of hundreds of thousands of rows takes far longer than the
            // default thirty seconds. VACUUM cannot run inside a transaction, and none is open here.
            db.Database.SetCommandTimeout(TimeSpan.FromHours(1));

            await db.Database.ExecuteSqlRawAsync("VACUUM (FULL, ANALYZE) \"Items\", \"Series\"", ct);
        }

        var after = await DatabaseSizeAsync(ct);

        logger.LogWarning("Compacted the cache tables: database went from {Before:N0} to {After:N0} bytes",
            before, after);

        return (before, after);
    }

    /// <summary>
    /// Episodes collapsed into one row per show, which is the shape both the list and its
    /// count are built from.
    /// </summary>
    private static IQueryable<SeriesGroup> Grouped(IQueryable<M3uItem> episodes) =>
        episodes
            .GroupBy(x => new { x.SeriesId, x.SearchTitle, x.Year })
            .Select(g => new SeriesGroup
            {
                SeriesId = g.Key.SeriesId,
                SearchTitle = g.Key.SearchTitle,
                Year = g.Key.Year,
                Title = g.Max(x => x.Title),
                Seasons = g.Select(x => x.Season).Distinct().Count(),
                Episodes = g.Count()
            });

    private sealed class SeriesGroup
    {
        public string? SeriesId { get; init; }
        public string SearchTitle { get; init; } = string.Empty;
        public int? Year { get; init; }
        public string? Title { get; init; }
        public int Seasons { get; init; }
        public int Episodes { get; init; }
    }

    private static string Key(string? seriesId, string searchTitle, int? year) =>
        $"{seriesId ?? "-"}~{searchTitle}~{year?.ToString() ?? "-"}";

    /// <summary>The entries of one show within one playlist, however that show is identified.</summary>
    private static IQueryable<M3uItem> Scope(AppDbContext db, CachedSeries series)
    {
        var query = db.Items.Where(x => x.SourceId == series.SourceId && x.Kind == MediaKind.Series);

        // Each written as two branches rather than one comparison, because a parameter holding
        // null compares as "= NULL", which is never true, and would match nothing at all.
        query = series.SeriesId is { } id
            ? query.Where(x => x.SeriesId == id)
            : query.Where(x => x.SeriesId == null && x.SearchTitle == series.SearchTitle);

        query = series.Year is { } year
            ? query.Where(x => x.Year == year)
            : query.Where(x => x.Year == null);

        return query;
    }

    /// <summary>The index rows behind these shows, for the ones a panel gave us an id for.</summary>
    private static IQueryable<M3uSeries> Indexed(AppDbContext db, IEnumerable<CachedSeries> series)
    {
        var known = series.Where(x => x.SeriesId is not null).ToList();

        if (known.Count == 0) return db.Series.Where(x => false);

        var sourceIds = known.Select(x => x.SourceId).Distinct().ToList();
        var seriesIds = known.Select(x => x.SeriesId!).Distinct().ToList();

        return db.Series.Where(x => sourceIds.Contains(x.SourceId) && seriesIds.Contains(x.SeriesId));
    }

    private static IQueryable<M3uItem> Episodes(AppDbContext db, int sourceId, string? filter) =>
        ApplyFilter(
            db.Items.AsNoTracking().Where(x => x.SourceId == sourceId && x.Kind == MediaKind.Series),
            filter);

    private static IQueryable<M3uItem> ApplyFilter(IQueryable<M3uItem> query, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return query;

        // Matched the way a search matches, so what this page lists is what a query would find.
        foreach (var token in SearchService.Tokenize(filter))
        {
            var needle = token;
            query = query.Where(x => EF.Functions.Like(x.SearchTitle, $"%{needle}%"));
        }

        return query;
    }

    /// <summary>
    /// Makes a series look unread. The row stays, because it came from the series list and
    /// would be back on the next refresh regardless; only the stamps go, and it is the stamps
    /// that decide whether a search bothers to fetch the episodes again.
    /// </summary>
    private static Task<int> ResetStampsAsync(AppDbContext db, IQueryable<M3uSeries> series, CancellationToken ct) =>
        series
            .Where(x => x.EpisodesFetchedFor != null || x.EpisodesFetchedAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.EpisodesFetchedFor, (long?)null)
                .SetProperty(x => x.EpisodesFetchedAt, (DateTimeOffset?)null), ct);
}
