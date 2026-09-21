using Smurfm3u.Core.Models;

namespace Smurfm3u.App.Services;

/// <summary>One row in the browser: a folder to step into, or a playlist to pick.</summary>
public sealed record BrowseEntry(
    string Name,
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

/// <summary>
/// One listing. <paramref name="Parent"/> is null at the root, where there is nowhere to go
/// back to, and <paramref name="Error"/> explains an empty list that is not simply empty.
/// </summary>
public sealed record BrowseResult(
    string RelativePath,
    string? Parent,
    IReadOnlyList<BrowseEntry> Entries,
    string? Error = null);

/// <summary>
/// Lists the playlist folder for the picker on the Playlists page. Confined to that folder:
/// the web UI has no business enumerating the rest of the container, so every path is put
/// through <see cref="BrowsePath"/> and anything landing outside falls back to the root.
/// </summary>
public class PlaylistBrowser(SettingsService settingsService, ILogger<PlaylistBrowser> logger)
{
    /// <summary>What counts as a playlist worth offering.</summary>
    private static readonly string[] Extensions = [".m3u", ".m3u8"];

    /// <summary>
    /// Opens on the folder holding <paramref name="absolutePath"/>, so editing an existing
    /// playlist starts where that file is rather than back at the top. A path from outside
    /// the playlist folder simply opens at the root.
    /// </summary>
    public async Task<BrowseResult> BrowseNearAsync(string? absolutePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return await BrowseAsync(null, ct);

        var settings = await settingsService.GetAsync(ct);
        var folder = Path.GetDirectoryName(absolutePath);

        if (string.IsNullOrWhiteSpace(settings.PlaylistPath) || string.IsNullOrWhiteSpace(folder))
            return await BrowseAsync(null, ct);

        return await BrowseAsync(BrowsePath.RelativeTo(settings.PlaylistPath, folder), ct);
    }

    public async Task<BrowseResult> BrowseAsync(string? relative, CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync(ct);
        var root = settings.PlaylistPath;

        if (string.IsNullOrWhiteSpace(root))
            return new BrowseResult(string.Empty, null, [], "No playlist directory is configured.");

        // A refused path is not worth an error message: it can only come from a stale link
        // or someone poking at it, and the root is a sane place to land either way.
        var target = BrowsePath.Resolve(root, relative) ?? BrowsePath.Resolve(root, null);

        if (target is null)
            return new BrowseResult(string.Empty, null, [], $"'{root}' is not a usable path.");

        if (!Directory.Exists(target))
        {
            return new BrowseResult(
                BrowsePath.RelativeTo(root, target),
                BrowsePath.ParentOf(root, target),
                [],
                target == BrowsePath.Resolve(root, null)
                    ? $"{root} does not exist. Mount it as a volume, or change the playlist directory in Settings."
                    : "That folder is no longer there.");
        }

        try
        {
            var entries = new List<BrowseEntry>();

            foreach (var directory in Directory.EnumerateDirectories(target))
                entries.Add(Describe(root, directory, isDirectory: true));

            foreach (var file in Directory.EnumerateFiles(target))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                entries.Add(Describe(root, file, isDirectory: false));
            }

            // Folders first, then playlists, each by name: the order someone expects to read.
            var ordered = entries
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new BrowseResult(
                BrowsePath.RelativeTo(root, target),
                BrowsePath.ParentOf(root, target),
                ordered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list {Path}", target);

            return new BrowseResult(
                BrowsePath.RelativeTo(root, target),
                BrowsePath.ParentOf(root, target),
                [],
                $"Could not read that folder: {ex.Message}");
        }
    }

    private static BrowseEntry Describe(string root, string path, bool isDirectory)
    {
        var info = new FileInfo(path);

        return new BrowseEntry(
            Path.GetFileName(path),
            BrowsePath.RelativeTo(root, path),
            path,
            isDirectory,
            isDirectory ? 0 : SizeOf(info),
            LastWriteOf(info));
    }

    /// <summary>A file that vanishes between listing and stat is reported as empty, not fatal.</summary>
    private static long SizeOf(FileInfo info)
    {
        try { return info.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static DateTimeOffset LastWriteOf(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }
}
