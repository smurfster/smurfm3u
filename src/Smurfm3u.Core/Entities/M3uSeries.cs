namespace Smurfm3u.Core.Entities;

/// <summary>
/// A series a panel offers, without its episodes.
/// <para>
/// The panel lists every series in one request and every episode list in one request each, so
/// this is the cheap half of its catalogue and the episodes are the expensive half. Keeping
/// the list locally is what makes a series findable; the episodes are fetched when a search
/// actually asks about that series, rather than all thirty thousand in advance.
/// </para>
/// </summary>
public class M3uSeries
{
    public long Id { get; set; }

    public int SourceId { get; set; }
    public M3uSource? Source { get; set; }

    /// <summary>The panel's own id, which is what its episode list is asked for by.</summary>
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>The name as the panel gives it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Cleaned of the year and the decoration, for showing and for release names.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Lowercased and stripped, for matching a query against. Indexed.</summary>
    public string SearchTitle { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>The panel's category, carried onto the episodes as their group.</summary>
    public string? GroupTitle { get; set; }

    public string? Cover { get; set; }

    /// <summary>What the panel currently says this series last changed at.</summary>
    public long? LastModified { get; set; }

    /// <summary>
    /// The <see cref="LastModified"/> the episodes on hand were fetched for, or null if they
    /// never have been. Different from the current value means the panel has changed the
    /// series since, so the episode list is worth fetching again.
    /// </summary>
    public long? EpisodesFetchedFor { get; set; }

    public DateTimeOffset? EpisodesFetchedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>False once the series leaves the panel; kept so its episodes still resolve.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>True when the episodes on hand are missing or out of date.</summary>
    public bool NeedsEpisodes => EpisodesFetchedFor is null || EpisodesFetchedFor != LastModified;
}
