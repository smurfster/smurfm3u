namespace Smurfm3u.Core.Models;

/// <summary>One row in a folder listing: somewhere to step into, or something to pick.</summary>
public sealed record BrowseItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

/// <summary>
/// One folder. <paramref name="Parent"/> is null at the top of the tree, and
/// <paramref name="Error"/> explains a listing that is empty for a reason.
/// </summary>
public sealed record FolderListing(
    string Path,
    string? Parent,
    IReadOnlyList<BrowseItem> Items,
    string? Error = null);

/// <summary>
/// Lists a folder for the path pickers. Browsing is deliberately not confined: every field
/// these pickers fill already accepts any path typed into it, and the service reads and
/// writes those paths, so a picker that could reach less than the box beside it would just
/// be a picker nobody could use.
/// </summary>
public static class FileBrowser
{
    /// <summary>
    /// Lists <paramref name="path"/>, or the root of the filesystem when it is blank.
    /// <paramref name="extensions"/> null means every file; an empty array means none, which
    /// is how a folder-only picker asks.
    /// </summary>
    public static FolderListing List(string? path, IReadOnlyList<string>? extensions = null)
    {
        var target = Normalize(path);
        var parent = ParentOf(target);

        if (!Directory.Exists(target))
            return new FolderListing(target, parent, [], $"{target} does not exist.");

        try
        {
            var items = new List<BrowseItem>();

            foreach (var directory in Directory.EnumerateDirectories(target))
                items.Add(Describe(directory, isDirectory: true));

            if (extensions is null || extensions.Count > 0)
            {
                foreach (var file in Directory.EnumerateFiles(target))
                {
                    if (extensions is not null
                        && !extensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;

                    items.Add(Describe(file, isDirectory: false));
                }
            }

            // Folders first, then files, each by name: the order someone expects to read.
            var ordered = items
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new FolderListing(target, parent, ordered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderListing(target, parent, [], $"Could not read that folder: {ex.Message}");
        }
    }

    /// <summary>
    /// The folder a path lives in, for opening a picker where its current value already is.
    /// A path that is itself a folder opens in that folder rather than above it.
    /// </summary>
    public static string StartingFolderFor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            if (Directory.Exists(value)) return Normalize(value);

            var folder = System.IO.Path.GetDirectoryName(Normalize(value));
            return string.IsNullOrWhiteSpace(folder) ? fallback : folder;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>The top of the tree, where there is nowhere further up.</summary>
    private static string? ParentOf(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return System.IO.Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";

        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable path lists as missing rather than throwing into a render.
            return path;
        }
    }

    private static BrowseItem Describe(string path, bool isDirectory)
    {
        var info = new FileInfo(path);

        return new BrowseItem(
            System.IO.Path.GetFileName(path),
            path,
            isDirectory,
            isDirectory ? 0 : Safely(() => info.Length),
            Safely(() => (DateTimeOffset)info.LastWriteTimeUtc));
    }

    /// <summary>A file that vanishes between listing and stat must not fail the whole listing.</summary>
    private static T Safely<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default!; }
    }
}
