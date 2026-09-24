using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Options;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

public sealed record SearchRequest(
    SearchKind Kind,
    string? Query,
    int? Season,
    int? Episode,
    IReadOnlyCollection<int> Categories,
    int Offset,
    int Limit,
    /// <summary>
    /// Which playlists to answer from. Empty means all of them, which is what the Newznab
    /// endpoint always sends: a client has no way to name one, and would not know what to
    /// name. The web UI does, and uses it to ask one playlist at a time.
    /// </summary>
    IReadOnlyCollection<int>? Sources = null);

/// <summary>
/// One offered release. <paramref name="DownloadId"/> is what a client grabs it by: a plain
/// entry id for a single episode or film, a <see cref="SeasonPackId"/> for a whole season.
/// <paramref name="Item"/> is the entry behind it, or a stand-in for the season.
/// </summary>
public sealed record SearchHit(
    M3uItem Item,
    string ReleaseName,
    long SizeBytes,
    int Category,
    int SubCategory,
    string DownloadId,
    int FileCount = 1);

/// <summary>
/// One answered query. <paramref name="Relaxed"/> says the words were not all found and the
/// closest matches were returned instead.
/// </summary>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, int Total, bool Relaxed)
{
    public static readonly SearchResults Empty = new([], 0, false);
}

/// <summary>
/// Answers Newznab queries out of the ingested playlist entries.
/// </summary>
public class SearchService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    SeriesBackfill backfill,
    SeasonPackService seasonPacks,
    ILogger<SearchService> logger)
{
    public const int MoviesCategory = 2000;
    public const int TvCategory = 5000;

    public async Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync(ct);

        // What was typed, tried exactly as typed.
        if (await AttemptAsync(request, settings, ct) is { } asWritten) return asWritten;

        // Nothing. "lanterns s01e01" and "sherlock & daughter s01" carry their season and
        // episode in the words, and left there they are terms the title has to contain, which
        // it never can: titles are stored with them stripped and the numbers in their own
        // columns. Read out, they narrow the search the way the *arrs' own parameters do.
        //
        // Second rather than first, because the two readings are not distinguishable by
        // shape. "Open Season 2" is a film and "Top Gear Season 2" is a season, and the only
        // thing that tells them apart is which one the catalogue actually has. Trying the
        // words as written first means a title that really does contain them always wins, and
        // this reading only gets its turn when the literal one found nothing at all.
        var interpreted = Interpret(request);

        if (interpreted != request && await AttemptAsync(interpreted, settings, ct) is { } reread)
        {
            logger.LogInformation("\"{Query}\" matched nothing; read as \"{Title}\" season {Season} episode {Episode}",
                request.Query, interpreted.Query, interpreted.Season, interpreted.Episode);

            return reread;
        }

        return await LastResortAsync(request, settings, ct);
    }

    /// <summary>
    /// One reading of a query, matched strictly. Null when nothing has every word, which is
    /// what lets the caller try the next reading before giving up.
    /// </summary>
    private async Task<SearchResults?> AttemptAsync(
        SearchRequest request, ServiceSettings settings, CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, settings.MaxSearchResults);

        var tokens = Tokenize(request.Query);

        // A panel's episodes are fetched when something asks about the series rather than all
        // of them in advance, so the asking is what brings them in. Only for a query naming
        // something: a browse has nothing to name, and a film search has no series to fetch.
        if (tokens.Count > 0 && request.Kind is SearchKind.Search or SearchKind.TvSearch)
            await backfill.EnsureEpisodesAsync(tokens, request.Sources, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var strict = ApplyTokens(Filtered(db, request), tokens);

        var ordered = tokens.Count == 0
            // An empty query is the *arrs' health check and the RSS feed; newest first is the useful answer.
            ? strict.OrderByDescending(x => x.FirstSeenAt).ThenBy(x => x.Id)
            : strict.OrderBy(x => x.Title).ThenBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id);

        var items = await ordered
            .Skip(Math.Max(0, request.Offset))
            .Take(limit)
            .ToListAsync(ct);

        if (items.Count > 0)
        {
            var packs = await PacksAsync(request, strict, settings, ct);

            return new SearchResults(
                [.. packs, .. Project(items, settings)],
                await strict.CountAsync(ct) + packs.Count,
                false);
        }

        // Nothing here. If the words name a panel series, what we hold of it is only as new as
        // the last refresh, so an episode added since would be invisible however often it was
        // asked for. A miss is reason enough to look again; the backfill decides how often.
        if (request.Kind is SearchKind.Search or SearchKind.TvSearch
            && await backfill.RecheckOnMissAsync(tokens, request.Sources, ct) > 0)
        {
            items = await ordered
                .Skip(Math.Max(0, request.Offset))
                .Take(limit)
                .ToListAsync(ct);

            if (items.Count > 0)
            {
                logger.LogInformation("\"{Query}\" was not here a moment ago; the panel has it now",
                    request.Query);

                var packs = await PacksAsync(request, strict, settings, ct);

                return new SearchResults(
                    [.. packs, .. Project(items, settings)],
                    await strict.CountAsync(ct) + packs.Count,
                    false);
            }
        }

        return null;
    }

    /// <summary>
    /// Every word has to appear, so one word the playlist does not use sinks the whole query.
    /// Once no reading of it has matched, answer with the closest entries rather than nothing.
    /// </summary>
    private async Task<SearchResults> LastResortAsync(
        SearchRequest request, ServiceSettings settings, CancellationToken ct)
    {
        var tokens = Tokenize(request.Query);
        var limit = Math.Clamp(request.Limit, 1, settings.MaxSearchResults);

        if (!settings.RelaxedSearchFallback || tokens.Count < 2)
            return SearchResults.Empty;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var near = await NearMatchesAsync(db, request, tokens, limit, ct);

        if (near.Count > 0)
            logger.LogInformation("No entry has every word of \"{Query}\"; returning {Count} near match(es)",
                request.Query, near.Count);

        return new SearchResults(Project(near, settings), near.Count, near.Count > 0);
    }

    /// <summary>
    /// The season-pack releases this search should be offered alongside its episodes.
    /// <para>
    /// Only for a season search - a client that named a season is asking about the season, and
    /// a pack is the answer to that question rather than to "which episodes do you have". Only
    /// on the first page, too: a pack covers the whole season however the episodes are paged,
    /// so repeating it on page two would just be the same release again.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> PacksAsync(
        SearchRequest request, IQueryable<M3uItem> matched, ServiceSettings settings, CancellationToken ct)
    {
        if (!settings.SeasonPacks) return [];
        if (request.Offset > 0) return [];
        if (request.Kind is not (SearchKind.Search or SearchKind.TvSearch)) return [];
        if (request.Season is not { } season || request.Episode is not null) return [];

        var packs = await seasonPacks.BuildAsync(matched, season, settings, ct);

        return packs.Select(pack =>
        {
            var representative = SeasonPackService.Representative(pack);
            var (category, subCategory) = CategoriesFor(representative);

            return new SearchHit(
                representative, pack.Name, pack.SizeBytes, category, subCategory,
                pack.Id.ToString(), pack.Episodes.Count);
        }).ToList();
    }

    /// <summary>
    /// Scores every entry that has at least one of the words and keeps only the best-scoring
    /// tier. A word scores its own length, so a long distinctive one such as "crunchlabs"
    /// outweighs a short common one such as "mark" without needing corpus statistics.
    /// </summary>
    private static async Task<List<M3uItem>> NearMatchesAsync(
        AppDbContext db, SearchRequest request, IReadOnlyList<string> tokens, int limit, CancellationToken ct)
    {
        var (anyMatch, score) = BuildRelaxation(tokens);

        var candidates = await Filtered(db, request)
            .Where(anyMatch)
            .OrderByDescending(score)
            .ThenBy(x => x.Title)
            .ThenBy(x => x.Season)
            .ThenBy(x => x.Episode)
            .ThenBy(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        // Ordering already put the best first; keeping only that tier stops a weak partial
        // match riding along behind a strong one.
        return SearchRelaxation.BestTier(candidates, x => x.SearchTitle, tokens).ToList();
    }

    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string)])!;

    /// <summary>
    /// Builds the two expressions the relaxed pass needs, so both are translated into one
    /// SQL statement rather than pulling candidate rows back to score them here.
    /// </summary>
    private static (Expression<Func<M3uItem, bool>> Any, Expression<Func<M3uItem, int>> Score)
        BuildRelaxation(IReadOnlyList<string> tokens)
    {
        var parameter = Expression.Parameter(typeof(M3uItem), "x");
        var searchTitle = Expression.Property(parameter, nameof(M3uItem.SearchTitle));
        var functions = Expression.Constant(EF.Functions, typeof(DbFunctions));

        Expression? any = null;
        Expression? score = null;

        foreach (var token in tokens)
        {
            var like = Expression.Call(
                LikeMethod, functions, searchTitle, Expression.Constant($"%{token}%"));

            any = any is null ? like : Expression.OrElse(any, like);

            var weight = Expression.Condition(
                like, Expression.Constant(token.Length), Expression.Constant(0));

            score = score is null ? weight : Expression.Add(score, weight);
        }

        return (
            Expression.Lambda<Func<M3uItem, bool>>(any!, parameter),
            Expression.Lambda<Func<M3uItem, int>>(score!, parameter));
    }

    /// <summary>
    /// The request with any season and episode lifted out of the query text. What the client
    /// sent explicitly always wins: a client that names them in both places means the
    /// parameters, and only a query that carries them alone is reinterpreted.
    /// </summary>
    private static SearchRequest Interpret(SearchRequest request)
    {
        if (request.Season is not null || request.Episode is not null) return request;

        var interpreted = SearchQuery.Interpret(request.Query);

        return interpreted.Season is null && interpreted.Episode is null
            ? request
            : request with
            {
                Query = interpreted.Text,
                Season = interpreted.Season,
                Episode = interpreted.Episode
            };
    }

    /// <summary>Everything except the words: what the request narrows to before matching.</summary>
    private static IQueryable<M3uItem> Filtered(AppDbContext db, SearchRequest request)
    {
        var query = db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .Where(x => x.IsActive && x.Source!.Enabled);

        // Named playlists only, when any were named. A disabled one stays out either way:
        // choosing it explicitly does not make it answer.
        if (request.Sources is { Count: > 0 } sources)
        {
            var chosen = sources.ToList();
            query = query.Where(x => chosen.Contains(x.SourceId));
        }

        if (ResolveKind(request) is { } wanted)
            query = query.Where(x => x.Kind == wanted);

        if (request.Season is { } season)
            query = query.Where(x => x.Season == season);

        if (request.Episode is { } episode)
            query = query.Where(x => x.Episode == episode);

        return query;
    }

    private static IQueryable<M3uItem> ApplyTokens(IQueryable<M3uItem> query, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            var needle = token;

            // A four-digit token is usually the release year the client appended to the title,
            // which lives in its own column rather than in the searchable title.
            if (needle.Length == 4 && int.TryParse(needle, out var year) && year is > 1900 and < 2100)
                query = query.Where(x => EF.Functions.Like(x.SearchTitle, $"%{needle}%") || x.Year == year);
            else
                query = query.Where(x => EF.Functions.Like(x.SearchTitle, $"%{needle}%"));
        }

        return query;
    }

    private static IReadOnlyList<SearchHit> Project(List<M3uItem> items, ServiceSettings settings) =>
        items.Select(item =>
        {
            var (category, subCategory) = CategoriesFor(item);
            return new SearchHit(
                item,
                ReleaseFactory.BuildName(item, item.Source),
                SizeEstimator.Estimate(item, settings),
                category,
                subCategory,
                item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }).ToList();

    /// <summary>
    /// The search mode fixes the media kind; where it does not, the requested categories do.
    /// Null means "no restriction".
    /// </summary>
    private static MediaKind? ResolveKind(SearchRequest request)
    {
        if (request.Kind == SearchKind.TvSearch) return MediaKind.Series;
        if (request.Kind == SearchKind.MovieSearch) return MediaKind.Movie;

        if (request.Categories.Count == 0) return null;

        var wantsTv = request.Categories.Any(c => c is >= 5000 and < 6000);
        var wantsMovies = request.Categories.Any(c => c is >= 2000 and < 3000);

        if (wantsTv && !wantsMovies) return MediaKind.Series;
        if (wantsMovies && !wantsTv) return MediaKind.Movie;
        return null;
    }

    /// <summary>
    /// Splits a query into normalized words. Every word must appear in the title, which keeps
    /// "top gear" from matching "Gear Top" while staying forgiving about punctuation.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? query)
    {
        var normalized = ReleaseTitleParser.Normalize(query);
        return normalized.Length == 0
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
    }

    /// <summary>Maps an entry onto a Newznab category and the resolution subcategory beneath it.</summary>
    public static (int Category, int SubCategory) CategoriesFor(M3uItem item)
    {
        var isTv = item.Kind == MediaKind.Series;
        var root = isTv ? TvCategory : MoviesCategory;

        var resolution = item.Source?.ResolutionTag ?? string.Empty;

        var sub = resolution.Contains("2160", StringComparison.OrdinalIgnoreCase)
                  || resolution.Contains("4k", StringComparison.OrdinalIgnoreCase)
            ? isTv ? 5045 : 2045
            : resolution.Contains("1080", StringComparison.OrdinalIgnoreCase)
              || resolution.Contains("720", StringComparison.OrdinalIgnoreCase)
                ? isTv ? 5040 : 2040
                : isTv ? 5030 : 2030;

        return (root, sub);
    }
}
