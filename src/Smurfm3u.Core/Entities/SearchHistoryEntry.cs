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

    public int ResultCount { get; set; }
    public int ElapsedMs { get; set; }

    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
