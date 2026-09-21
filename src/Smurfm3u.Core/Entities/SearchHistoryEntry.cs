namespace Smurfm3u.Core.Entities;

/// <summary>One Newznab query, recorded so the Searches page can show what Prowlarr is asking for.</summary>
public class SearchHistoryEntry
{
    public long Id { get; set; }

    public SearchKind Kind { get; set; }

    public string? Query { get; set; }

    public int? Season { get; set; }
    public int? Episode { get; set; }

    public string? ImdbId { get; set; }
    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }

    /// <summary>Raw newznab category string as sent by the client, e.g. "5030,5040".</summary>
    public string? Categories { get; set; }

    /// <summary>Paging as the client asked for it, which is what tells one page of a walk from the next.</summary>
    public int Offset { get; set; }
    public int Limit { get; set; }

    public int ResultCount { get; set; }
    public int ElapsedMs { get; set; }

    /// <summary>The words were not all found and the closest entries were returned instead.</summary>
    public bool Relaxed { get; set; }

    /// <summary>
    /// The playlist entries this query answered with, in the order they were sent. Ids rather
    /// than names, because a name is rebuilt from the entry and its playlist tags and would
    /// otherwise be stored a hundred times over.
    /// </summary>
    public List<long> ResultItemIds { get; set; } = [];

    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
