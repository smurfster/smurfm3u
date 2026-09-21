using Smurfm3u.Core.Entities;

namespace Smurfm3u.Core.Parsing;

public sealed record VodVerdict(bool IsVod, MediaKind Hint, string Extension, string Reason);

/// <summary>
/// Decides whether a playlist entry is on-demand content or a live channel.
/// Nothing in the M3U format marks this, so we score the signals that providers
/// actually emit: URL path segments, container extension, runtime and group name.
/// </summary>
public static class VodClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "avi", "mov", "m4v", "mpg", "mpeg", "wmv", "flv", "webm", "divx", "ogm"
    };

    // Seen as live far more often than as VOD, so they need corroborating evidence.
    private static readonly HashSet<string> StreamingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "m3u8", "ts", "mpd"
    };

    private static readonly string[] LiveGroupMarkers =
    [
        "24/7", "24-7", "live", "channel", "radio", "ppv", "event", "sport", "news"
    ];

    private static readonly string[] SeriesGroupMarkers =
    [
        "series", "serie", "show", "tv show", "box set", "boxset", "season"
    ];

    private static readonly string[] MovieGroupMarkers =
    [
        "movie", "film", "cinema", "vod"
    ];

    public static VodVerdict Classify(M3uEntry entry)
    {
        var path = SafePath(entry.Url);
        var extension = ExtensionOf(path);
        var group = entry.GroupTitle ?? string.Empty;

        // Xtream-codes and most panel software segment the path by content type.
        // This is the strongest signal available, so it decides on its own.
        if (PathHasSegment(path, "live"))
            return new VodVerdict(false, MediaKind.Unknown, extension, "url path marks it live");

        if (PathHasSegment(path, "series"))
            return new VodVerdict(true, MediaKind.Series, NormalizeExtension(extension), "url path marks it a series");

        if (PathHasSegment(path, "movie") || PathHasSegment(path, "movies"))
            return new VodVerdict(true, MediaKind.Movie, NormalizeExtension(extension), "url path marks it a movie");

        var groupHint = HintFromGroup(group);

        if (VideoExtensions.Contains(extension))
            return new VodVerdict(true, groupHint, extension, "file extension is a video container");

        var looksLiveByGroup = LiveGroupMarkers.Any(m => group.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (StreamingExtensions.Contains(extension))
        {
            // A runtime or an explicit VOD group is enough to rescue an HLS/TS url.
            if (entry.DurationSeconds > 0 && !looksLiveByGroup)
                return new VodVerdict(true, groupHint, "mp4", "streaming url with a known runtime");

            if (groupHint != MediaKind.Unknown && !looksLiveByGroup)
                return new VodVerdict(true, groupHint, "mp4", "streaming url in an on-demand group");

            return new VodVerdict(false, MediaKind.Unknown, extension, "streaming url with no on-demand signal");
        }

        if (looksLiveByGroup)
            return new VodVerdict(false, MediaKind.Unknown, extension, "group name marks it live");

        if (entry.DurationSeconds > 0)
            return new VodVerdict(true, groupHint, NormalizeExtension(extension), "entry declares a runtime");

        if (groupHint != MediaKind.Unknown)
            return new VodVerdict(true, groupHint, NormalizeExtension(extension), "group name marks it on-demand");

        return new VodVerdict(false, MediaKind.Unknown, extension, "no on-demand signal");
    }

    private static MediaKind HintFromGroup(string group)
    {
        if (group.Length == 0) return MediaKind.Unknown;
        if (SeriesGroupMarkers.Any(m => group.Contains(m, StringComparison.OrdinalIgnoreCase))) return MediaKind.Series;
        if (MovieGroupMarkers.Any(m => group.Contains(m, StringComparison.OrdinalIgnoreCase))) return MediaKind.Movie;
        return MediaKind.Unknown;
    }

    private static string SafePath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath;

        var q = url.IndexOf('?');
        return q >= 0 ? url[..q] : url;
    }

    private static bool PathHasSegment(string path, string segment) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private static string ExtensionOf(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return string.Empty;

        var ext = name[(dot + 1)..];
        return ext.Length is > 0 and <= 5 && ext.All(char.IsLetterOrDigit) ? ext.ToLowerInvariant() : string.Empty;
    }

    /// <summary>Falls back to mp4 so downloads always land with a container the *arrs will import.</summary>
    private static string NormalizeExtension(string extension) =>
        VideoExtensions.Contains(extension) ? extension.ToLowerInvariant() : "mp4";
}
