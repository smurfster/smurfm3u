namespace Smurfm3u.Core.Xtream;

/// <summary>Which of the panel's stream paths an id belongs under.</summary>
public enum XtreamStreamKind
{
    Movie,
    Series,
    Live
}

/// <summary>
/// Where a panel is and who we are on it. Built from whatever the provider handed out, which
/// is rarely tidy: a bare host with no scheme, a base URL with a trailing slash, or the whole
/// get.php line with the credentials already in the query string.
/// </summary>
public sealed record XtreamCredentials(string BaseUrl, string Username, string Password)
{
    /// <summary>Endpoint names a provider might leave on the end of the URL they give you.</summary>
    private static readonly string[] KnownEndpoints =
        ["get.php", "player_api.php", "panel_api.php", "xmltv.php", "portal.php"];

    /// <summary>
    /// Builds a usable address, or explains what is missing. Credentials in the URL are only
    /// used when the fields are empty, so what is typed in the form always wins.
    /// </summary>
    public static bool TryCreate(
        string? location, string? username, string? password,
        out XtreamCredentials credentials, out string error)
    {
        credentials = null!;

        var raw = (location ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "Enter the panel's address.";
            return false;
        }

        // A host on its own is the most common thing to paste, and it is not a URL yet.
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "http://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "That is not an http or https address.";
            return false;
        }

        var user = FirstFilled(username, ValueFromQuery(uri, "username"));
        var pass = FirstFilled(password, ValueFromQuery(uri, "password"));

        if (user.Length == 0 || pass.Length == 0)
        {
            error = "Enter the username and password for the panel.";
            return false;
        }

        credentials = new XtreamCredentials(BaseFrom(uri), user, pass);
        error = string.Empty;
        return true;
    }

    /// <summary>A player_api.php call, with the credentials and an optional action attached.</summary>
    public string Api(string? action = null, params (string Key, string Value)[] parameters)
    {
        var query = new List<string>
        {
            "username=" + Uri.EscapeDataString(Username),
            "password=" + Uri.EscapeDataString(Password)
        };

        if (!string.IsNullOrWhiteSpace(action))
            query.Add("action=" + Uri.EscapeDataString(action));

        foreach (var (key, value) in parameters)
            query.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));

        return $"{BaseUrl}/player_api.php?{string.Join('&', query)}";
    }

    /// <summary>
    /// Where one stream actually lives. The credentials sit in the path rather than the query
    /// here, which is the panel's design, not ours; they are escaped so a password with a
    /// slash or a space in it cannot come apart.
    /// </summary>
    public string StreamUrl(XtreamStreamKind kind, string id, string? extension)
    {
        var segment = kind switch
        {
            XtreamStreamKind.Series => "series",
            XtreamStreamKind.Live => "live",
            _ => "movie"
        };

        // Lowercased here as well as in the catalogue, so the extension in the URL and the one
        // recorded against the entry cannot disagree about case whoever built them.
        var ext = string.IsNullOrWhiteSpace(extension)
            ? "mp4"
            : extension.Trim().TrimStart('.').ToLowerInvariant();

        return $"{BaseUrl}/{segment}/{Uri.EscapeDataString(Username)}/{Uri.EscapeDataString(Password)}/{id}.{ext}";
    }

    /// <summary>
    /// Scheme, host and port, plus any directory the panel is served from. The endpoint file
    /// is dropped: a provider hands out the get.php line, and everything is built from the
    /// same base, so leaving it on would ask for /get.php/player_api.php.
    /// </summary>
    private static string BaseFrom(Uri uri)
    {
        var path = uri.AbsolutePath;

        foreach (var endpoint in KnownEndpoints)
        {
            if (!path.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase)) continue;

            path = path[..^endpoint.Length];
            break;
        }

        return uri.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/');
    }

    private static string ValueFromQuery(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return string.Empty;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0) continue;

            if (pair[..split].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(split + 1)..]);
        }

        return string.Empty;
    }

    private static string FirstFilled(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
}
