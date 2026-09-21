using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Data;

namespace Smurfm3u.App.Api;

/// <summary>
/// A generic Newznab indexer over the ingested playlists. Prowlarr points at this, and Sonarr
/// and Radarr search through Prowlarr and grab the pseudo-nzb that <c>t=get</c> returns.
/// </summary>
[ApiController]
[Route("api")]
[Route("newznab/api")]
public class NewznabController(
    SearchService search,
    SabnzbdHandler sab,
    SettingsService settingsService,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider clock,
    ILogger<NewznabController> logger) : ControllerBase
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] string? t,
        [FromQuery] string? apikey,
        [FromQuery] string? q,
        [FromQuery] int? season,
        [FromQuery] int? ep,
        [FromQuery] string? cat,
        [FromQuery] long? id,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        // Sonarr and Radarr default to an empty SABnzbd url base and so call /api too.
        // A "mode" parameter is unique to the SABnzbd API, which makes the two safe to share.
        if (Request.Query.ContainsKey("mode"))
            return Ok(await sab.HandleAsync(await SabnzbdController.ReadRequestAsync(HttpContext), ct));

        var settings = await settingsService.GetAsync(ct);

        if (!string.Equals(apikey, settings.ApiKey, StringComparison.Ordinal))
            return NewznabError(100, "Incorrect user credentials");

        var mode = (t ?? "search").ToLowerInvariant();

        return mode switch
        {
            "caps" => Caps(),
            "get" or "download" => await GetNzbAsync(id, ct),
            "search" => await SearchAsync(SearchKind.Search, q, null, null, cat, offset, limit, ct),
            "tvsearch" => await SearchAsync(SearchKind.TvSearch, q, season, ep, cat, offset, limit, ct),
            "movie" => await SearchAsync(SearchKind.MovieSearch, q, null, null, cat, offset, limit, ct),
            _ => NewznabError(202, $"No such function ({mode})")
        };
    }

    /// <summary>
    /// Only the SABnzbd side posts, and only to upload an nzb. Newznab itself is GET-only.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct) =>
        Ok(await sab.HandleAsync(await SabnzbdController.ReadRequestAsync(HttpContext), ct));

    private IActionResult Caps()
    {
        // Only parameters we can actually honour are advertised. We have no tvdb/imdb mapping,
        // so claiming those ids would make Sonarr and Radarr send queries we cannot answer.
        var caps = new XElement("caps",
            new XElement("server",
                new XAttribute("version", "1.0"),
                new XAttribute("title", "Smurfm3u"),
                new XAttribute("strapline", "VOD playlists as a Newznab indexer"),
                new XAttribute("url", BaseUrl())),
            new XElement("limits", new XAttribute("max", "200"), new XAttribute("default", "100")),
            new XElement("retention", new XAttribute("days", "9999")),
            new XElement("registration", new XAttribute("available", "no"), new XAttribute("open", "no")),
            new XElement("searching",
                new XElement("search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q")),
                new XElement("tv-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,season,ep")),
                new XElement("movie-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q")),
                new XElement("audio-search", new XAttribute("available", "no"), new XAttribute("supportedParams", "")),
                new XElement("book-search", new XAttribute("available", "no"), new XAttribute("supportedParams", ""))),
            new XElement("categories",
                new XElement("category",
                    new XAttribute("id", "2000"), new XAttribute("name", "Movies"),
                    new XElement("subcat", new XAttribute("id", "2030"), new XAttribute("name", "SD")),
                    new XElement("subcat", new XAttribute("id", "2040"), new XAttribute("name", "HD")),
                    new XElement("subcat", new XAttribute("id", "2045"), new XAttribute("name", "UHD"))),
                new XElement("category",
                    new XAttribute("id", "5000"), new XAttribute("name", "TV"),
                    new XElement("subcat", new XAttribute("id", "5030"), new XAttribute("name", "SD")),
                    new XElement("subcat", new XAttribute("id", "5040"), new XAttribute("name", "HD")),
                    new XElement("subcat", new XAttribute("id", "5045"), new XAttribute("name", "UHD")))));

        return Xml(new XDocument(new XDeclaration("1.0", "utf-8", null), caps));
    }

    private async Task<IActionResult> SearchAsync(
        SearchKind kind, string? q, int? season, int? ep, string? cat, int offset, int limit, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var categories = ParseCategories(cat);

        var request = new SearchRequest(kind, q, season, ep, categories, offset, limit);

        var hits = await search.SearchAsync(request, ct);
        var total = await search.CountAsync(request, ct);
        var apiKey = (await settingsService.GetAsync(ct)).ApiKey;

        await RecordSearchAsync(kind, q, season, ep, cat, hits.Count, Stopwatch.GetElapsedTime(started), ct);

        var channel = new XElement("channel",
            new XElement(Atom + "link",
                new XAttribute("href", BaseUrl() + "/api"),
                new XAttribute("rel", "self"),
                new XAttribute("type", "application/rss+xml")),
            new XElement("title", "Smurfm3u"),
            new XElement("description", "VOD playlists as a Newznab indexer"),
            new XElement("link", BaseUrl()),
            new XElement("language", "en-gb"),
            new XElement(Newznab + "response",
                new XAttribute("offset", offset),
                new XAttribute("total", total)));

        foreach (var hit in hits)
            channel.Add(BuildItem(hit, apiKey));

        var rss = new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "atom", Atom.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "newznab", Newznab.NamespaceName),
            channel);

        return Xml(new XDocument(new XDeclaration("1.0", "utf-8", null), rss));
    }

    private XElement BuildItem(SearchHit hit, string apiKey)
    {
        var link = $"{BaseUrl()}/api?t=get&id={hit.Item.Id}&apikey={Uri.EscapeDataString(apiKey)}";
        var published = hit.Item.FirstSeenAt.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);

        var element = new XElement("item",
            new XElement("title", hit.ReleaseName),
            new XElement("guid", new XAttribute("isPermaLink", "true"), link),
            new XElement("link", link),
            new XElement("comments", link),
            new XElement("pubDate", published),
            new XElement("category", hit.SubCategory),
            new XElement("description", hit.Item.RawTitle),
            new XElement("enclosure",
                new XAttribute("url", link),
                new XAttribute("length", hit.SizeBytes),
                new XAttribute("type", "application/x-nzb")),
            Attr("category", hit.Category),
            Attr("category", hit.SubCategory),
            Attr("size", hit.SizeBytes),
            Attr("grabs", 0),
            Attr("files", 1));

        if (hit.Item.Season is { } season)
            element.Add(Attr("season", $"S{season:D2}"));

        if (hit.Item.Episode is { } episode)
            element.Add(Attr("episode", $"E{episode:D2}"));

        return element;

        static XElement Attr(string name, object value) =>
            new(Newznab + "attr", new XAttribute("name", name), new XAttribute("value", value));
    }

    /// <summary>
    /// Returns the pseudo-nzb for one playlist entry. This is what a grab downloads and then
    /// posts to our SABnzbd endpoint, which is how a search result becomes a queued download.
    /// </summary>
    private async Task<IActionResult> GetNzbAsync(long? id, CancellationToken ct)
    {
        if (id is not { } itemId)
            return NewznabError(200, "Missing parameter (id)");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .FirstOrDefaultAsync(x => x.Id == itemId, ct);

        if (item is null)
            return NewznabError(300, "No such item");

        var settings = await settingsService.GetAsync(ct);
        var name = Core.Parsing.ReleaseFactory.BuildName(item, item.Source);
        var size = SizeEstimator.Estimate(item, settings);

        logger.LogInformation("Serving nzb for {Name}", name);

        var payload = System.Text.Encoding.UTF8.GetBytes(NzbDocument.Build(item.Id, name, size));
        return File(payload, "application/x-nzb", $"{name}.nzb");
    }

    private async Task RecordSearchAsync(
        SearchKind kind, string? q, int? season, int? ep, string? cat,
        int resultCount, TimeSpan elapsed, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            db.Searches.Add(new SearchHistoryEntry
            {
                Kind = kind,
                Query = q,
                Season = season,
                Episode = ep,
                Categories = cat,
                ResultCount = resultCount,
                ElapsedMs = (int)elapsed.TotalMilliseconds,
                ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                RequestedAt = clock.GetUtcNow()
            });

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // History is a convenience; never fail a search because we could not log it.
            logger.LogWarning(ex, "Could not record search history");
        }
    }

    private static IReadOnlyCollection<int> ParseCategories(string? cat)
    {
        if (string.IsNullOrWhiteSpace(cat)) return [];

        return cat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var value) ? value : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();
    }

    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    private IActionResult NewznabError(int code, string description)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("error",
                new XAttribute("code", code),
                new XAttribute("description", description)));

        // Newznab reports failures in the body with a 200, which is what clients expect.
        return Xml(document);
    }

    private ContentResult Xml(XDocument document) =>
        Content(document.Declaration + Environment.NewLine + document, "application/xml", System.Text.Encoding.UTF8);
}
