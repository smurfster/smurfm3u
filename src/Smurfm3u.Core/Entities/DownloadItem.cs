namespace Smurfm3u.Core.Entities;

/// <summary>
/// One grab. Surfaces as a SABnzbd queue slot while active and a history slot once finished.
/// </summary>
public class DownloadItem
{
    public long Id { get; set; }

    /// <summary>The id we hand back to Sonarr/Radarr, e.g. "SABnzbd_nzo_a1b2c3".</summary>
    public string NzoId { get; set; } = string.Empty;

    /// <summary>Release name, i.e. what the *arr apps parse and import against.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SAB category the client asked for ("tv", "movies", ...).</summary>
    public string Category { get; set; } = string.Empty;

    public long? M3uItemId { get; set; }
    public M3uItem? M3uItem { get; set; }

    public int? SourceId { get; set; }
    public M3uSource? Source { get; set; }

    public string StreamUrl { get; set; } = string.Empty;

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    /// <summary>Lower runs first; SAB priorities map onto this.</summary>
    public int Priority { get; set; }

    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }

    /// <summary>Instantaneous rate in bytes/sec, refreshed while downloading.</summary>
    public long BytesPerSecond { get; set; }

    public string? IncompletePath { get; set; }
    public string? CompletedPath { get; set; }

    public string? FailureMessage { get; set; }
    public int AttemptCount { get; set; }

    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused;
}
