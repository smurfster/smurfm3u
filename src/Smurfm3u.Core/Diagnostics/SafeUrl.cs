namespace Smurfm3u.Core.Diagnostics;

/// <summary>
/// Takes the credentials out of a URL before it is written anywhere someone might read it.
/// <para>
/// A provider's playlist link is normally a get.php with the username and password in its
/// query string, so logging a playlist's location verbatim would put a password in the log -
/// which is now a page in the web UI - and in the body of a refresh-failed email.
/// </para>
/// </summary>
public static class SafeUrl
{
    private const string Mask = "***";

    /// <summary>Query keys whose value is never worth writing down.</summary>
    private static readonly string[] Secrets =
        ["password", "pass", "pwd", "token", "apikey", "api_key", "secret", "auth"];

    /// <summary>
    /// The same location with anything secret masked. Anything that is not a URL - a local
    /// file path, a blank - comes back untouched, because there is nothing in it to hide.
    /// </summary>
    public static string Redact(string? location)
    {
        // Blank or whitespace both read as nothing in a log line, so both come back as nothing.
        if (string.IsNullOrWhiteSpace(location)) return string.Empty;
        if (!Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri)) return location.Trim();
        if (uri.IsFile) return location.Trim();

        var builder = new UriBuilder(uri);

        // http://user:pass@host is the other place credentials hide.
        if (!string.IsNullOrEmpty(builder.Password)) builder.Password = Mask;

        builder.Query = RedactQuery(uri.Query);

        // UriBuilder re-adds the default port as an explicit one, which changes what is read
        // back even though it means the same thing.
        return builder.Uri.IsDefaultPort
            ? builder.Uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Port, UriFormat.UriEscaped)
            : builder.Uri.AbsoluteUri;
    }

    private static string RedactQuery(string query)
    {
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0) return string.Empty;

        var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var split = part.IndexOf('=');

            if (split <= 0)
            {
                kept.Add(part);
                continue;
            }

            var key = part[..split];
            kept.Add(IsSecret(key) ? $"{key}={Mask}" : part);
        }

        return string.Join('&', kept);
    }

    private static bool IsSecret(string key)
    {
        foreach (var secret in Secrets)
            if (key.Equals(secret, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
