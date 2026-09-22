using System.Net;
using Smurfm3u.Core.Options;

namespace Smurfm3u.App.Services;

/// <summary>
/// The proxy every outbound HTTP call is handed. The handlers are built once at startup, so
/// the current settings are held here as a snapshot and swapped whenever settings are saved:
/// turning a proxy on or off takes effect on the next request rather than at the next restart.
/// </summary>
/// <remarks>
/// It is its own <see cref="ICredentials"/> as well. The handler reads <see cref="Credentials"/>
/// once and then asks it per connection, so returning ourselves is what keeps a changed
/// username or password from needing a restart too.
/// </remarks>
public sealed class ProxyProvider : IWebProxy, ICredentials
{
    private readonly ILogger<ProxyProvider> logger;

    /// <summary>What GetProxy answers with, or null to go direct. Replaced whole, never edited.</summary>
    private volatile Snapshot? current;

    public ProxyProvider(SettingsService settings, ILogger<ProxyProvider> logger)
    {
        this.logger = logger;
        settings.Changed += s => Apply(s.Proxy);
    }

    /// <summary>Reads the stored settings once, before anything can make a request.</summary>
    public static async Task PrimeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var settings = await services.GetRequiredService<SettingsService>().GetAsync(ct);
        services.GetRequiredService<ProxyProvider>().Apply(settings.Proxy);
    }

    public void Apply(ProxySettings settings)
    {
        var next = Build(settings);

        if (next is null && settings.Enabled)
            logger.LogWarning("Proxy is switched on but its address is not usable; going direct");

        // Same shape either way, so the log reads the same whichever direction it changed in.
        if (next?.Address != current?.Address)
            logger.LogInformation("Outbound HTTP now goes {Route}",
                next is null ? "direct" : $"through {next.Address}");

        current = next;
    }

    /// <summary>Null when the proxy is off or unusable, which the handler reads as direct.</summary>
    private static Snapshot? Build(ProxySettings settings)
    {
        if (!settings.Enabled) return null;
        if (settings.BuildAddress() is not { } address) return null;

        var credential = settings.HasCredentials
            ? new NetworkCredential(settings.Username, settings.Password ?? string.Empty)
            : null;

        return new Snapshot(address, credential, settings);
    }

    ICredentials? IWebProxy.Credentials
    {
        get => this;

        // The handler never sets this, and there is nowhere for it to go if it did: the
        // credentials belong to the stored settings.
        set { }
    }

    public Uri? GetProxy(Uri destination) => current?.Address;

    public bool IsBypassed(Uri host) =>
        current is not { } snapshot || snapshot.Settings.IsBypassed(host.Host);

    public NetworkCredential? GetCredential(Uri uri, string authType) => current?.Credential;

    private sealed record Snapshot(Uri Address, NetworkCredential? Credential, ProxySettings Settings);
}

public static class ProxyProviderExtensions
{
    /// <summary>
    /// Routes a named client through <see cref="ProxyProvider"/>. The handler is built once and
    /// then asks per request, which is what lets the proxy be changed while the service runs.
    /// </summary>
    public static IHttpClientBuilder UseConfiguredProxy(this IHttpClientBuilder builder) =>
        builder.ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
        {
            Proxy = sp.GetRequiredService<ProxyProvider>(),
            UseProxy = true
        });
}
