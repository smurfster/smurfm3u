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
public class XtreamClient(IHttpClientFactory httpClientFactory, TimeProvider clock, ILogger<XtreamClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Series between progress lines during the episode walk.</summary>
    private const int ProgressEvery = 200;

    /// <summary>Retries of a refused request before it counts as a real failure.</summary>
    private const int MaxAttempts = 4;

    private readonly Lock gate = new();

    /// <summary>When the panel may be asked again. Shared by every request in the walk.</summary>
    private DateTimeOffset resumeAt = DateTimeOffset.MinValue;

    /// <summary>
    /// How many series are read at once, moved up and down by how the panel is answering.
    /// Per client, and the client is created per refresh, so it starts optimistic each time.
    /// </summary>
    private readonly XtreamPace pace = new(SeriesBatchSize);

    /// <summary>
    /// How many series are asked about at once. The episode list is one request per series and
    /// a panel may hold thousands, so this is the difference between an hour and a few minutes;
    /// it stays small because the other end is someone's IPTV box, not a CDN.
    /// </summary>
    private const int SeriesBatchSize = 4;

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
    public async IAsyncEnumerable<IngestCandidate> EnumerateAsync(
        M3uSource source, XtreamCredentials credentials, [EnumeratorCancellation] CancellationToken ct = default)
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

        foreach (var movie in movies)
        {
            if (string.IsNullOrWhiteSpace(movie.StreamId) || string.IsNullOrWhiteSpace(movie.Name)) continue;

            yield return XtreamCatalogue.ForMovie(movie, Lookup(movieCategories, movie.CategoryId), credentials);
        }

        if (!source.IncludeSeries)
        {
            logger.LogInformation("{Source}: series not requested, films only", source.Name);
            yield break;
        }

        var seriesCategories = XtreamCatalogue.NameById(
            await GetListAsync<XtreamCategory>(client, credentials.Api("get_series_categories"), headers, "get_series_categories", ct));

        var series = await GetListAsync<XtreamSeries>(
            client, credentials.Api("get_series"), headers, "get_series", ct);

        logger.LogInformation(
            "{Source}: panel lists {Series} in {Categories}; reading episode lists {Batch} at a time",
            source.Name, Count(series.Count, "series", "series"),
            Count(seriesCategories.Count, "category", "categories"), CurrentBatchSize);

        var readable = series.Where(x => !string.IsNullOrWhiteSpace(x.SeriesId)).ToList();
        var done = 0;
        var nextReport = ProgressEvery;

        // Taken a slice at a time rather than pre-chunked, because the size shrinks if the
        // panel starts refusing and a chunked sequence has already decided how it is split.
        while (done < readable.Count)
        {
            ct.ThrowIfCancellationRequested();

            var take = Math.Min(CurrentBatchSize, readable.Count - done);
            var batch = readable.GetRange(done, take);

            var infos = await Task.WhenAll(batch.Select(x => SeriesInfoAsync(client, credentials, x, headers, ct)));

            for (var i = 0; i < batch.Count; i++)
            {
                if (infos[i] is not { } info) continue;

                foreach (var candidate in XtreamCatalogue.ForSeries(
                             batch[i], info, Lookup(seriesCategories, batch[i].CategoryId), credentials))
                {
                    yield return candidate;
                }
            }

            done += batch.Count;

            // A panel with thousands of series takes a while, and a silent hour reads as a hang.
            // Counted to the next milestone rather than checked for a multiple: the batch size
            // changes with the panel's mood, so a run of threes steps straight over every
            // multiple of two hundred and the walk goes quiet while it is still working.
            if (done >= nextReport)
            {
                logger.LogInformation("{Source}: {Done} of {Total} series read", source.Name, done, readable.Count);

                while (nextReport <= done) nextReport += ProgressEvery;
            }
        }
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
