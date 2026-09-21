using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
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
/// Answers Newznab queries out of the ingested playlist entries.
/// </summary>
public class SearchService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService)
{
    public const int MoviesCategory = 2000;
    public const int TvCategory = 5000;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync(ct);
        var limit = Math.Clamp(request.Limit, 1, settings.MaxSearchResults);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .Where(x => x.IsActive && x.Source!.Enabled);

        var kind = ResolveKind(request);
        if (kind is { } wanted)
            query = query.Where(x => x.Kind == wanted);

        if (request.Season is { } season)
            query = query.Where(x => x.Season == season);

        if (request.Episode is { } episode)
            query = query.Where(x => x.Episode == episode);

        var tokens = Tokenize(request.Query);
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

        query = tokens.Count == 0
            // An empty query is the *arrs' health check and the RSS feed; newest first is the useful answer.
            ? query.OrderByDescending(x => x.FirstSeenAt).ThenBy(x => x.Id)
            : query.OrderBy(x => x.Title).ThenBy(x => x.Season).ThenBy(x => x.Episode).ThenBy(x => x.Id);

        var items = await query
            .Skip(Math.Max(0, request.Offset))
            .Take(limit)
            .ToListAsync(ct);

        return items.Select(item =>
        {
            var (category, subCategory) = CategoriesFor(item);
            return new SearchHit(
                item,
                ReleaseFactory.BuildName(item, item.Source),
                SizeEstimator.Estimate(item, settings),
                category,
                subCategory);
        }).ToList();
    }

    /// <summary>Total matches ignoring paging, for the newznab:response element.</summary>
    public async Task<int> CountAsync(SearchRequest request, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Items.AsNoTracking().Where(x => x.IsActive && x.Source!.Enabled);

        if (ResolveKind(request) is { } wanted)
            query = query.Where(x => x.Kind == wanted);

        if (request.Season is { } season)
            query = query.Where(x => x.Season == season);

        if (request.Episode is { } episode)
            query = query.Where(x => x.Episode == episode);

        foreach (var token in Tokenize(request.Query))
        {
            var needle = token;
            if (needle.Length == 4 && int.TryParse(needle, out var year) && year is > 1900 and < 2100)
                query = query.Where(x => EF.Functions.Like(x.SearchTitle, $"%{needle}%") || x.Year == year);
            else
                query = query.Where(x => EF.Functions.Like(x.SearchTitle, $"%{needle}%"));
        }

        return await query.CountAsync(ct);
    }

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
