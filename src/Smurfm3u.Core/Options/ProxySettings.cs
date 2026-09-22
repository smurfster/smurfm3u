namespace Smurfm3u.Core.Options;

/// <summary>
/// Protocols a proxy can speak. The value is the URI scheme the HTTP stack expects, so the
/// names double as what goes in front of the host.
/// </summary>
public enum ProxyKind
{
    Http = 0,
    Socks5 = 1,
    Socks4a = 2,
    Socks4 = 3
}

/// <summary>
/// One outbound proxy, used for playlist fetches and for downloads. Notifications go out over
/// SMTP and do not pass through it.
/// </summary>
public class ProxySettings
{
    public bool Enabled { get; set; }

    public ProxyKind Kind { get; set; } = ProxyKind.Http;

    /// <summary>Host name or IP. A pasted "socks5://host:1080" is tolerated and picked apart.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Ignored when <see cref="Host"/> carries its own port.</summary>
    public int Port { get; set; } = 1080;

    /// <summary>Leave blank for a proxy that needs no login.</summary>
    public string? Username { get; set; }

    /// <summary>Stored as written, because the proxy has to be presented with it. See the README.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Hosts that go direct instead of through the proxy, one per line or separated by commas.
    /// <c>*</c> matches any run of characters, and a pattern starting with <c>.</c> matches the
    /// domain itself as well as everything under it.
    /// </summary>
    public string? BypassHosts { get; set; }

    /// <summary>True when this is switched on and has enough to build an address from.</summary>
    public bool IsUsable => Enabled && BuildAddress() is not null;

    public static string SchemeFor(ProxyKind kind) => kind switch
    {
        ProxyKind.Socks5 => "socks5",
        ProxyKind.Socks4a => "socks4a",
        ProxyKind.Socks4 => "socks4",
        _ => "http"
    };

    public static string Describe(ProxyKind kind) => kind switch
    {
        ProxyKind.Socks5 => "SOCKS5",
        ProxyKind.Socks4a => "SOCKS4a",
        ProxyKind.Socks4 => "SOCKS4",
        _ => "HTTP"
    };

    /// <summary>
    /// The address the HTTP stack connects to, or null when the host is blank or unusable.
    /// The scheme always comes from <see cref="Kind"/>: one pasted into the host field is
    /// dropped, because the dropdown is what the user can see.
    /// </summary>
    public Uri? BuildAddress()
    {
        var host = (Host ?? string.Empty).Trim();
        if (host.Length == 0) return null;

        // Tolerate a whole URL being pasted in: strip the scheme and anything after the host.
        var scheme = host.IndexOf("//", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 2)..];

        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];

        var port = Port;
        if (SplitPort(ref host, out var typedPort)) port = typedPort;

        if (host.Length == 0 || port is <= 0 or > 65535) return null;

        // A bare IPv6 literal needs brackets before it can go in a URI.
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";

        return Uri.TryCreate($"{SchemeFor(Kind)}://{host}:{port}", UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>Credentials to present, or null when the proxy is anonymous.</summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username);

    /// <summary>True when this host should go direct rather than through the proxy.</summary>
    public bool IsBypassed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        foreach (var pattern in BypassPatterns())
            if (Matches(pattern, host))
                return true;

        return false;
    }

    /// <summary>The bypass list split up, tolerating commas, semicolons, spaces and newlines.</summary>
    public IReadOnlyList<string> BypassPatterns() =>
        (BypassHosts ?? string.Empty)
        .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>
    /// Takes a trailing ":port" off the host. An IPv6 literal is only split when it is
    /// bracketed, because an unbracketed one is all colons and none of them is a port.
    /// </summary>
    private static bool SplitPort(ref string host, out int port)
    {
        port = 0;

        var colon = host.StartsWith('[')
            ? host.IndexOf(':', host.IndexOf(']') + 1)
            : host.IndexOf(':');

        if (colon < 0 || host.IndexOf(':', colon + 1) >= 0) return false;
        if (!int.TryParse(host[(colon + 1)..], out port)) return false;

        host = host[..colon];
        return true;
    }

    /// <summary>
    /// Wildcard match against a host name. Anchored at both ends, so "example.com" matches only
    /// itself and "*.example.com" only its subdomains.
    /// </summary>
    private static bool Matches(string pattern, string host)
    {
        if (pattern == "*") return true;

        // ".example.com" is the usual shorthand for the domain and everything under it.
        if (pattern.StartsWith('.'))
            return host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
                   || host.Equals(pattern[1..], StringComparison.OrdinalIgnoreCase);

        var parts = pattern.Split('*');
        var at = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            var found = host.IndexOf(part, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;

            // The first segment has to sit at the start, or the pattern was not anchored there.
            if (i == 0 && found != 0) return false;

            at = found + part.Length;
        }

        // Likewise the last segment has to reach the end.
        return parts[^1].Length == 0 || at == host.Length;
    }
}
