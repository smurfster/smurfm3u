namespace Smurfm3u.Core.Entities;

/// <summary>
/// A single VOD entry from a playlist, plus the parsed metadata we use to answer Newznab searches.
/// </summary>
public class M3uItem
{
    public long Id { get; set; }

    public int SourceId { get; set; }
    public M3uSource? Source { get; set; }

    /// <summary>Stable identity within a source: hash of the stream URL. Used to dedupe across refreshes.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>The raw #EXTINF display name, exactly as the playlist had it.</summary>
    public string RawTitle { get; set; } = string.Empty;

    public string StreamUrl { get; set; } = string.Empty;

    public string? GroupTitle { get; set; }
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
    public string? TvgLogo { get; set; }

    /// <summary>Runtime in seconds from #EXTINF, when the playlist provides one.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>
    /// Which series this episode belongs to on the panel it came from. Null for a film and for
    /// anything read out of a playlist file. It is what lets a refresh mark a whole series as
    /// still present without asking the panel about it again.
    /// </summary>
    public string? SeriesId { get; set; }

    /// <summary>
    /// What the panel said the series was last changed at, as it said it. Compared against the
    /// next refresh to decide whether the episode list is worth fetching at all.
    /// </summary>
    public long? SeriesLastModified { get; set; }

    public MediaKind Kind { get; set; }

    /// <summary>Cleaned show/film title, e.g. "Top Gear".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Lowercased, punctuation-stripped title for matching. Indexed.</summary>
    public string SearchTitle { get; set; } = string.Empty;

    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? EpisodeTitle { get; set; }

    /// <summary>Container extension without the dot, e.g. "mkv". Defaults to mp4 when unknown.</summary>
    public string Extension { get; set; } = "mp4";

    /// <summary>Content-Length observed on the last HEAD, or 0 when unknown.</summary>
    public long SizeBytes { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>False once the entry disappears from the playlist; kept so history stays readable.</summary>
    public bool IsActive { get; set; } = true;
}
