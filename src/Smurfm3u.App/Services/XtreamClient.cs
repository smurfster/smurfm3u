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
public class XtreamClient(IHttpClientFactory httpClientFactory, ILogger<XtreamClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

        await AuthenticateAsync(credentials, headers, ct);

        var movieCategories = XtreamCatalogue.NameById(
            await GetListAsync<XtreamCategory>(client, credentials.Api("get_vod_categories"), headers, "get_vod_categories", ct));

        var movies = await GetListAsync<XtreamVodStream>(
            client, credentials.Api("get_vod_streams"), headers, "get_vod_streams", ct);

        logger.LogInformation("{Source}: panel lists {Count} films", source.Name, movies.Count);

        foreach (var movie in movies)
        {
            if (string.IsNullOrWhiteSpace(movie.StreamId) || string.IsNullOrWhiteSpace(movie.Name)) continue;

            yield return XtreamCatalogue.ForMovie(movie, Lookup(movieCategories, movie.CategoryId), credentials);
        }

        if (!source.IncludeSeries) yield break;

        var seriesCategories = XtreamCatalogue.NameById(
            await GetListAsync<XtreamCategory>(client, credentials.Api("get_series_categories"), headers, "get_series_categories", ct));

        var series = await GetListAsync<XtreamSeries>(
            client, credentials.Api("get_series"), headers, "get_series", ct);

        logger.LogInformation("{Source}: panel lists {Count} series, fetching episodes", source.Name, series.Count);

        var done = 0;

        foreach (var batch in series.Where(x => !string.IsNullOrWhiteSpace(x.SeriesId)).Chunk(SeriesBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var infos = await Task.WhenAll(batch.Select(x => SeriesInfoAsync(client, credentials, x, headers, ct)));

            for (var i = 0; i < batch.Length; i++)
            {
                if (infos[i] is not { } info) continue;

                foreach (var candidate in XtreamCatalogue.ForSeries(
                             batch[i], info, Lookup(seriesCategories, batch[i].CategoryId), credentials))
                {
                    yield return candidate;
                }
            }

            done += batch.Length;

            // A panel with thousands of series takes a while, and a silent hour reads as a hang.
            if (done % 200 == 0)
                logger.LogInformation("{Source}: {Done} of {Total} series read", source.Name, done, series.Count);
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

    private static async Task<T?> GetAsync<T>(
        HttpClient client, string url, string? headers, string action, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        M3uRefreshService.ApplyHeaders(request, headers);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

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

    private static string? Lookup(Dictionary<string, string> categories, string? id) =>
        id is not null && categories.TryGetValue(id, out var name) ? name : null;
}
