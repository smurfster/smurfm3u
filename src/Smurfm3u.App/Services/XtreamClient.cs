using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Core.Xtream;

namespace Smurfm3u.App.Services;

/// <summary>
/// Reads a panel through its player API. Uses the same HTTP client as a playlist fetch, so a
/// configured proxy carries these calls too.
/// </summary>
public class XtreamClient(
    IHttpClientFactory httpClientFactory,
    RefreshProgress progress,
    TimeProvider clock,
    ILogger<XtreamClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Films between updates of the live progress the page watches.</summary>
    private const int ProgressSamples = 500;

    /// <summary>Retries of a refused request before it counts as a real failure.</summary>
    private const int MaxAttempts = 4;

    private readonly Lock gate = new();

    /// <summary>When the panel may be asked again. Shared by every request in the walk.</summary>
    private DateTimeOffset resumeAt = DateTimeOffset.MinValue;

    /// <summary>
    /// How many series are read at once, moved up and down by how the panel is answering and
    /// capped by the playlist's own ceiling. Replaced when a walk starts, once the source being
    /// walked is known; the client is created per refresh, so it starts optimistic each time.
    /// </summary>
    private XtreamPace pace = new(DefaultSeriesConcurrency);

    /// <summary>
    /// What a playlist asks for when it has no opinion. The other end is someone's IPTV box
    /// rather than a CDN, so the default is small and raising it is a per-playlist decision.
    /// </summary>
    private const int DefaultSeriesConcurrency = 4;

    /// <summary>
    /// Checks the login and returns what the panel says about the account. Panels answer 200
    /// with auth 0 rather than 401, so the body has to be read to know whether it worked.
    /// </summary>
    public async Task<XtreamUserInfo> AuthenticateAsync(
        XtreamCredentials credentials, string? headers, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("playlist");
        var auth = await GetAsync<XtreamAuth>(client, credentials.Api(), headers, "authenticate", ct);

        var info = auth?.UserInfo
                   ?? throw new InvalidOperationException(
                       "The panel did not answer with account details. Check the address is its root, not a stream link.");

        if (!info.IsAuthenticated)
            throw new InvalidOperationException("The panel rejected that username and password.");

        if (!info.IsActive)
            throw new InvalidOperationException($"The panel reports this account as {info.Status}.");

        return info;
    }

    /// <summary>
    /// Every on-demand entry the panel offers: the films, and the episodes of every series
    /// when the source asks for them. Nothing live is requested, so unlike a playlist there is
    /// nothing to filter back out afterwards.
    /// </summary>
    public async IAsyncEnumerable<IngestCandidate> EnumerateFilmsAsync(
        M3uSource source,
        XtreamCredentials credentials,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("playlist");
        var headers = source.Headers;

        var account = await AuthenticateAsync(credentials, headers, ct);

        logger.LogInformation(
            "{Source}: panel at {Panel} accepted {User}, account {Status}{Connections}",
            source.Name, credentials.BaseUrl, credentials.Username,
            string.IsNullOrWhiteSpace(account.Status) ? "active" : account.Status,
            account.MaxConnections is { Length: > 0 } max ? $", {max} connections" : string.Empty);

        var movieCategories = XtreamCatalogue.NameById(
            await GetListAsync<XtreamCategory>(client, credentials.Api("get_vod_categories"), headers, "get_vod_categories", ct));

        var movies = await GetListAsync<XtreamVodStream>(
            client, credentials.Api("get_vod_streams"), headers, "get_vod_streams", ct);

        logger.LogInformation("{Source}: panel lists {Films} in {Categories}",
            source.Name, Count(movies.Count, "film", "films"), Count(movieCategories.Count, "category", "categories"));

        progress.Begin(source.Id, "films", movies.Count);
        var films = 0;

        foreach (var movie in movies)
        {
            if (++films % ProgressSamples == 0) progress.Report(source.Id, films);

            if (string.IsNullOrWhiteSpace(movie.StreamId) || string.IsNullOrWhiteSpace(movie.Name)) continue;

            yield return XtreamCatalogue.ForMovie(movie, Lookup(movieCategories, movie.CategoryId), credentials);
        }
    }

    /// <summary>
    /// Every series the panel offers, as a list, without their episodes. One request, a few
    /// seconds, and the cheap half of the catalogue.
    /// </summary>
    public async Task<(IReadOnlyList<XtreamSeries> Series, IReadOnlyDictionary<string, string> Categories)>
        ListSeriesAsync(M3uSource source, XtreamCredentials credentials, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("playlist");

        var categories = XtreamCatalogue.NameById(await GetListAsync<XtreamCategory>(
            client, credentials.Api("get_series_categories"), source.Headers, "get_series_categories", ct));

        var series = await GetListAsync<XtreamSeries>(
            client, credentials.Api("get_series"), source.Headers, "get_series", ct);

        return (series, categories);
    }

    /// <summary>
    /// One series' episodes, asked for because something actually wants them. This is the
    /// request the old refresh made thirty thousand times in advance.
    /// </summary>
    public async Task<IReadOnlyList<IngestCandidate>> EpisodesAsync(
        M3uSource source,
        XtreamCredentials credentials,
        XtreamSeries series,
        string? category,
        CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("playlist");
        var info = await SeriesInfoAsync(client, credentials, series, source.Headers, ct);

        return info is null ? [] : XtreamCatalogue.ForSeries(series, info, category, credentials).ToList();
    }

    /// <summary>
    /// One series' episode list. A series that fails is logged and skipped rather than taking
    /// the whole refresh down: one bad entry out of thousands is not worth losing the rest.
    /// </summary>
    private async Task<XtreamSeriesInfo?> SeriesInfoAsync(
        HttpClient client, XtreamCredentials credentials, XtreamSeries series, string? headers, CancellationToken ct)
    {
        var url = credentials.Api("get_series_info", ("series_id", series.SeriesId!));

        try
        {
            return await GetAsync<XtreamSeriesInfo>(client, url, headers, "get_series_info", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read episodes for series {Series} ({Id})", series.Name, series.SeriesId);
            return null;
        }
    }

    private async Task<List<T>> GetListAsync<T>(
        HttpClient client, string url, string? headers, string action, CancellationToken ct) =>
        await GetAsync<List<T>>(client, url, headers, action, ct) ?? [];

    private async Task<T?> GetAsync<T>(
        HttpClient client, string url, string? headers, string action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Applies whether or not this request is the one that was refused: a panel that is
            // counting requests wants all of them to stop, not just the unlucky one.
            await WaitOutAnyPauseAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            M3uRefreshService.ApplyHeaders(request, headers);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode
                && XtreamBackoff.IsWorthRetrying((int)response.StatusCode)
                && attempt <= MaxAttempts)
            {
                var wait = XtreamBackoff.For(
                    attempt,
                    response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null,
                    clock.GetUtcNow());

                Pause(wait);

                logger.LogWarning(
                    "Panel answered {Status} to {Action}; waiting {Wait:0.#}s before attempt {Next} of {Max}",
                    (int)response.StatusCode, action, wait.TotalSeconds, attempt + 1, MaxAttempts + 1);

                continue;
            }

            response.EnsureSuccessStatusCode();
            RecordCleanAnswer();

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, ct);
            }
            catch (JsonException ex)
            {
                // Panels answer an unsupported action with an error object, or with HTML from a
                // reverse proxy. Either way the action is worth naming; the raw body is not.
                throw new InvalidOperationException(
                    $"The panel's answer to {action} was not what the Xtream API describes.", ex);
            }
        }
    }

    /// <summary>
    /// Holds every request until the pause a refusal set has elapsed. Without this the other
    /// requests already in flight carry on into a panel that has just asked for quiet, and
    /// each of them earns another refusal.
    /// </summary>
    private async Task WaitOutAnyPauseAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan remaining;

            lock (gate)
            {
                remaining = resumeAt - clock.GetUtcNow();
            }

            if (remaining <= TimeSpan.Zero) return;

            await Task.Delay(remaining, clock, ct);
        }
    }

    /// <summary>
    /// Starts the pause and eases off the episode walk. A panel that refused four at once will
    /// refuse the next four as well, so carrying on at the same rate turns the walk into
    /// waiting; it climbs back on its own once the panel is answering cleanly again.
    /// </summary>
    private void Pause(TimeSpan wait)
    {
        bool eased;
        int batch;

        lock (gate)
        {
            var now = clock.GetUtcNow();

            // The requests in flight together are refused together, and that is one push-back
            // rather than four. Counting each of them would have the pace treat a single busy
            // moment as four, and grow four times as reluctant to speed up again afterwards.
            var alreadyKnown = resumeAt > now;

            var until = now + wait;
            if (until > resumeAt) resumeAt = until;

            eased = !alreadyKnown && pace.Refused();
            batch = pace.Batch;
        }

        if (eased)
            logger.LogInformation("Panel is pushing back; easing off to {Batch} series at a time", batch);
    }

    /// <summary>
    /// A clean answer, which is what earns the pace back. Counted for every call rather than
    /// only the episode lists, because they all come out of the same allowance.
    /// </summary>
    private void RecordCleanAnswer()
    {
        bool faster;
        int batch;

        lock (gate)
        {
            faster = pace.Answered();
            batch = pace.Batch;
        }

        if (faster)
            logger.LogInformation("Panel is answering cleanly; going back up to {Batch} series at a time", batch);
    }

    private static string? Lookup(Dictionary<string, string> categories, string? id) =>
        id is not null && categories.TryGetValue(id, out var name) ? name : null;

    /// <summary>The size to take next, under the lock because requests in flight are moving it.</summary>
    private int CurrentBatchSize
    {
        get { lock (gate) return pace.Batch; }
    }

    /// <summary>"1 category" rather than "1 categories"; these lines are meant to be read.</summary>
    private static string Count(int number, string singular, string plural) =>
        $"{number:N0} {(number == 1 ? singular : plural)}";
}
