using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.App.Services;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Options;
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
    SearchHistoryService history,
    SeasonPackService seasonPacks,
    SabnzbdHandler sab,
    SettingsService settingsService,
    IDbContextFactory<AppDbContext> dbFactory,
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
        // A string, because a release is either one entry's id or a whole season's. A single
        // entry still reads as the bare number it always was, so links already out there work.
        [FromQuery] string? id,
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
        var settings = await settingsService.GetAsync(ct);

        // An empty query is a feed of what is newest, not a result set to be walked. Clients
        // keep asking for the next page until a short one comes back, so leaving this
        // uncapped costs a round trip every couple of seconds until they hit their own page
        // ceiling, fetching entries nobody asked for.
        var feedCap = string.IsNullOrWhiteSpace(q) ? settings.RssFeedLimit : 0;
        var honoured = feedCap > 0 ? Math.Clamp(feedCap - offset, 0, limit) : limit;

        var results = honoured <= 0
            ? SearchResults.Empty
            : await search.SearchAsync(
                new SearchRequest(kind, q, season, ep, categories, offset, honoured), ct);

        var hits = results.Hits;
        var total = feedCap > 0 ? Math.Min(results.Total, feedCap) : results.Total;
        var apiKey = settings.ApiKey;

        await RecordSearchAsync(
            kind, q, season, ep, cat, offset, limit, hits, results.Relaxed,
            Stopwatch.GetElapsedTime(started), ct);

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
        var link = $"{BaseUrl()}/api?t=get&id={Uri.EscapeDataString(hit.DownloadId)}"
                   + $"&apikey={Uri.EscapeDataString(apiKey)}";
        var published = hit.Item.FirstSeenAt.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);

        var element = new XElement("item",
            new XElement("title", hit.ReleaseName),
            new XElement("guid", new XAttribute("isPermaLink", "true"), link),
            new XElement("link", link),
            // Sonarr and Radarr open this one when you click through to a release, so it goes
            // to a page describing what the release holds rather than to the nzb itself -
            // which downloaded a file and told you nothing. It carries no api key: the page
            // is behind the same sign-in as the rest of the UI, and a key in here would sit
            // in the client's database and on its screen for no reason.
            new XElement("comments", $"{BaseUrl()}/release/{Uri.EscapeDataString(hit.DownloadId)}"),
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
            Attr("files", hit.FileCount));

        if (hit.Item.Season is { } season)
            element.Add(Attr("season", $"S{season:D2}"));

        // Left off a season pack on purpose. A release with a season and no episode is how
        // Sonarr is told this covers the whole season rather than one entry in it.
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
    private async Task<IActionResult> GetNzbAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NewznabError(200, "Missing parameter (id)");

        var settings = await settingsService.GetAsync(ct);

        return SeasonPackId.TryParse(id, out var packId)
            ? await SeasonNzbAsync(packId, settings, ct)
            : await EntryNzbAsync(id, settings, ct);
    }

    private async Task<IActionResult> EntryNzbAsync(string id, ServiceSettings settings, CancellationToken ct)
    {
        if (!long.TryParse(id, System.Globalization.CultureInfo.InvariantCulture, out var itemId))
            return NewznabError(300, "No such item");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .FirstOrDefaultAsync(x => x.Id == itemId, ct);

        if (item is null)
            return NewznabError(300, "No such item");

        var name = Core.Parsing.ReleaseFactory.BuildName(item, item.Source);
        var size = SizeEstimator.Estimate(item, settings);

        logger.LogInformation("Serving nzb for {Name}", name);

        var payload = System.Text.Encoding.UTF8.GetBytes(NzbDocument.Build(item.Id, name, size));
        return File(payload, "application/x-nzb", $"{name}.nzb");
    }

    /// <summary>
    /// The pseudo-nzb for a whole season: one entry per episode, in episode order. Resolved
    /// now rather than at search time, so a grab that arrives hours later gets the season as
    /// it stands today.
    /// </summary>
    private async Task<IActionResult> SeasonNzbAsync(
        SeasonPackId packId, ServiceSettings settings, CancellationToken ct)
    {
        var pack = await seasonPacks.ResolveAsync(packId, settings, ct);

        if (pack is null)
            return NewznabError(300, "No such item");

        logger.LogInformation("Serving nzb for {Name}: {Count} episodes", pack.Name, pack.Episodes.Count);

        var document = NzbDocument.Build(
            [.. pack.Episodes.Select(x => x.Id)],
            [.. pack.Episodes.Select(x => Core.Parsing.ReleaseFactory.BuildName(x, x.Source))],
            pack.Name,
            pack.SizeBytes);

        return File(System.Text.Encoding.UTF8.GetBytes(document), "application/x-nzb", $"{pack.Name}.nzb");
    }

    /// <summary>
    /// Records what a client of the indexer asked for. The Search page records its own through
    /// the same service, so both land in one history and can be told apart by their origin.
    /// </summary>
    private Task RecordSearchAsync(
        SearchKind kind, string? q, int? season, int? ep, string? cat,
        int offset, int limit, IReadOnlyList<SearchHit> hits, bool relaxed, TimeSpan elapsed,
        CancellationToken ct) =>
        history.RecordAsync(
            new SearchHistoryEntry
            {
                Origin = SearchOrigin.Indexer,
                Kind = kind,
                Query = q,
                Season = season,
                Episode = ep,
                Categories = cat,
                Offset = offset,
                Limit = limit,
                ResultCount = hits.Count,
                Relaxed = relaxed,
                ElapsedMs = (int)elapsed.TotalMilliseconds,
                ClientIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null
            },
            hits.Select(x => x.Item.Id),
            ct);

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
