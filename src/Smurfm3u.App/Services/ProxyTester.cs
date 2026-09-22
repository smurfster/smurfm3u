using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Options;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>Outcome of a proxy test, worded for someone looking at the settings page.</summary>
public sealed record ProxyTestResult(bool Ok, string Message);

/// <summary>
/// Checks a proxy with the settings as they are on screen, before anything is saved. Where
/// there is a remote playlist to aim at it fetches through the proxy for real, because a
/// reachable port proves nothing about the protocol or the credentials.
/// </summary>
public class ProxyTester(IDbContextFactory<AppDbContext> dbFactory, ILogger<ProxyTester> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<ProxyTestResult> TestAsync(ProxySettings settings, CancellationToken ct = default)
    {
        if (settings.BuildAddress() is not { } address)
            return new ProxyTestResult(false, "Fill in the proxy address and port first.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Sources
            .AsNoTracking()
            .Where(x => x.Enabled && x.Kind == M3uSourceKind.Remote)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

        try
        {
            return target is null
                ? await ReachableAsync(address, ct)
                : await FetchAsync(settings, address, target, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProxyTestResult(false, $"{address} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Proxy test against {Proxy} failed", address);
            return new ProxyTestResult(false, $"{ProxySettings.Describe(settings.Kind)} proxy {address} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The real thing: a request for the playlist, through the proxy, with the playlist's own
    /// headers. Any answer at all means the tunnel stood up, so the status is reported rather
    /// than judged &mdash; a provider that says 403 to a bare fetch has still been reached.
    /// </summary>
    private static async Task<ProxyTestResult> FetchAsync(
        ProxySettings settings, Uri address, M3uSource target, CancellationToken ct)
    {
        if (Uri.TryCreate(target.Location, UriKind.Absolute, out var location) && settings.IsBypassed(location.Host))
            return new ProxyTestResult(false,
                $"{location.Host} is in the bypass list, so it would go direct. Nothing to test against.");

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(address)
            {
                Credentials = settings.HasCredentials
                    ? new NetworkCredential(settings.Username, settings.Password ?? string.Empty)
                    : null
            },
            UseProxy = true
        };

        using var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Smurfm3u/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, target.Location);
        M3uRefreshService.ApplyHeaders(request, target.Headers);

        var started = Stopwatch.GetTimestamp();

        // Headers only: the playlist behind this URL may be hundreds of megabytes.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);

        return new ProxyTestResult(true,
            $"Fetched {target.Name} through {address} in {elapsed.TotalSeconds:0.0}s " +
            $"and got HTTP {(int)response.StatusCode} {response.StatusCode}.");
    }

    /// <summary>
    /// Fallback when there is no remote playlist to aim at. Only proves something is listening,
    /// which is said plainly rather than dressed up as a passing test.
    /// </summary>
    private static async Task<ProxyTestResult> ReachableAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, timeout.Token);

        return new ProxyTestResult(true,
            $"{address.Host}:{address.Port} is accepting connections. Add a remote playlist to " +
            "test the protocol and credentials as well.");
    }
}
