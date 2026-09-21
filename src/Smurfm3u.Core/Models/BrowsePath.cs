namespace Smurfm3u.Core.Models;

/// <summary>
/// Keeps a file browser inside the folder it was given. Everything a browser is asked to
/// open goes through here first, so a crafted path cannot walk out into the rest of the
/// container and list it.
/// </summary>
public static class BrowsePath
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolves <paramref name="relative"/> under <paramref name="root"/>. Returns null when
    /// the result would land outside the root, which the caller treats as "go back to the
    /// root" rather than as an error worth explaining to whoever asked.
    /// </summary>
    public static string? Resolve(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        var fullRoot = Normalize(root);

        // Combine, not append: a rooted "relative" would otherwise silently replace the root.
        var candidate = string.IsNullOrWhiteSpace(relative)
            ? fullRoot
            : Normalize(Path.Combine(fullRoot, relative));

        if (candidate.Equals(fullRoot, Comparison)) return candidate;

        // The separator matters: "/playlists-other" must not pass as being under "/playlists".
        return candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, Comparison)
            ? candidate
            : null;
    }

    /// <summary>
    /// The path of <paramref name="full"/> as seen from the root, using forward slashes so it
    /// survives a round trip through a URL or a form field. Empty means the root itself.
    /// </summary>
    public static string RelativeTo(string root, string full)
    {
        var fullRoot = Normalize(root);
        var target = Normalize(full);

        if (target.Equals(fullRoot, Comparison)) return string.Empty;

        return Path.GetRelativePath(fullRoot, target).Replace('\\', '/');
    }

    /// <summary>The folder above, or null at the root, where there is nowhere further up.</summary>
    public static string? ParentOf(string root, string full)
    {
        var fullRoot = Normalize(root);
        var target = Normalize(full);

        if (target.Equals(fullRoot, Comparison)) return null;

        var parent = Path.GetDirectoryName(target);
        return parent is null ? null : RelativeTo(fullRoot, parent);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
