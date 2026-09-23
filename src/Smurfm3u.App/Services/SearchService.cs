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
    int Limit);

public sealed record SearchHit(
    M3uItem Item,
    string ReleaseName,
    long SizeBytes,
    int Category,
    int SubCategory);

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
    ILogger<SearchService> logger)
{
    public const int MoviesCategory = 2000;
    public const int TvCategory = 5000;

    public async Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync(ct);
        var limit = Math.Clamp(request.Limit, 1, settings.MaxSearchResults);
        var tokens = Tokenize(request.Query);

        // A panel's episodes are fetched when something asks about the series rather than all
        // of them in advance, so the asking is what brings them in. Only for a query naming
        // something: a browse has nothing to name, and a film search has no series to fetch.
        if (tokens.Count > 0 && request.Kind is SearchKind.Search or SearchKind.TvSearch)
            await backfill.EnsureEpisodesAsync(tokens, ct);

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
            return new SearchResults(Project(items, settings), await strict.CountAsync(ct), false);

        // Every word has to appear, so one word the playlist does not use sinks the whole
        // query. Rather than answer nothing, fall back to the closest entries we do have.
        if (!settings.RelaxedSearchFallback || tokens.Count < 2)
            return SearchResults.Empty;

        var near = await NearMatchesAsync(db, request, tokens, limit, ct);

        if (near.Count > 0)
            logger.LogInformation("No entry has every word of \"{Query}\"; returning {Count} near match(es)",
                request.Query, near.Count);

        return new SearchResults(Project(near, settings), near.Count, near.Count > 0);
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

    /// <summary>Everything except the words: what the request narrows to before matching.</summary>
    private static IQueryable<M3uItem> Filtered(AppDbContext db, SearchRequest request)
    {
        var query = db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .Where(x => x.IsActive && x.Source!.Enabled);

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
                subCategory);
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
