namespace Smurfm3u.Core.Options;

/// <summary>
/// Rewrites one path prefix in what Smurfm3u reports to Sonarr and Radarr, for when the same
/// files sit at a different path there. Nothing about where this service reads and writes
/// changes; only the paths it hands out do.
/// </summary>
public class PathMapping
{
    /// <summary>The path as this service sees it, e.g. <c>/downloads/complete</c>.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The same location as the *arr app sees it, e.g. <c>/mnt/smurfm3u/complete</c>.</summary>
    public string To { get; set; } = string.Empty;
}

/// <summary>
/// Applies <see cref="PathMapping"/>s to a path on its way out to a client. This is the same
/// idea as an *arr Remote Path Mapping, done from our side so it is configured once here
/// rather than separately in every app that talks to us.
/// </summary>
public static class PathMapper
{
    /// <summary>
    /// Rewrites <paramref name="path"/> using the first mapping that matches it, longest
    /// <see cref="PathMapping.From"/> first so a mapping for a subfolder beats one for its
    /// parent. Returns the path untouched when nothing matches, which is the normal case for
    /// an install whose clients see the same paths it does.
    /// </summary>
    public static string Apply(string? path, IReadOnlyCollection<PathMapping>? mappings)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
        if (mappings is null || mappings.Count == 0) return path;

        var candidates = mappings
            .Select(m => (From: Trim(m.From), To: Trim(m.To)))
            .Where(m => m.From.Length > 0 && m.To.Length > 0)
            .OrderByDescending(m => m.From.Length);

        foreach (var (from, to) in candidates)
        {
            if (!path.StartsWith(from, StringComparison.Ordinal)) continue;

            var rest = path[from.Length..];

            // A prefix only counts on a segment boundary, so /downloads never matches
            // /downloads-old and a mapping cannot cut a folder name in half.
            if (rest.Length > 0 && rest[0] is not ('/' or '\\')) continue;

            // Follow the separator the target uses, so a client on Windows is given
            // Windows separators all the way down rather than a mix of both.
            rest = to.Contains('\\') && !to.Contains('/')
                ? rest.Replace('/', '\\')
                : rest.Replace('\\', '/');

            return to + rest;
        }

        return path;
    }

    /// <summary>A trailing separator is noise; matching and rebuilding both ignore it.</summary>
    private static string Trim(string? value) =>
        value?.Trim().TrimEnd('/', '\\') ?? string.Empty;
}
