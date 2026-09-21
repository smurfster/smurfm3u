namespace Smurfm3u.Core.Options;

/// <summary>
/// Runtime settings editable from the web UI. Persisted as a single JSON row so adding
/// a field never needs a migration.
/// </summary>
public class ServiceSettings
{
    /// <summary>Shared by the Newznab and SABnzbd endpoints; this is what Prowlarr and the *arrs send.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Where in-progress files are written.</summary>
    public string IncompletePath { get; set; } = "/downloads/incomplete";

    /// <summary>Where finished downloads are moved for the *arr apps to import.</summary>
    public string CompletePath { get; set; } = "/downloads/complete";

    /// <summary>Where notifications go, and which events send one.</summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>
    /// Rewrites the paths reported to Sonarr and Radarr when they see the same files
    /// somewhere else. Empty means they see what we see, which needs no mapping.
    /// </summary>
    public List<PathMapping> PathMappings { get; set; } = [];

    /// <summary>Ceiling across all sources; each source also has its own cap.</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>Manual global cap in KiB/s. 0 = unlimited, matching SABnzbd's convention.</summary>
    public int ManualSpeedLimitKibps { get; set; }

    public int SearchHistoryRetentionDays { get; set; } = 30;
    public int DownloadHistoryRetentionDays { get; set; } = 90;

    /// <summary>Category the *arrs use for TV; surfaced in the SABnzbd config response.</summary>
    public string TvCategory { get; set; } = "tv";
    public string MovieCategory { get; set; } = "movies";

    /// <summary>Cap on results returned for one Newznab query.</summary>
    public int MaxSearchResults { get; set; } = 200;

    /// <summary>
    /// Used to estimate release size when the playlist does not declare one.
    /// The *arrs reject zero-byte releases and use size to pick between them.
    /// </summary>
    public int AssumedBitrateKbps { get; set; } = 4500;

    /// <summary>Fallback size in MiB when neither a real size nor a runtime is known.</summary>
    public int FallbackSizeMib { get; set; } = 2048;

    /// <summary>Retry a failed download this many times before it lands in history as failed.</summary>
    public int MaxDownloadAttempts { get; set; } = 3;

    /// <summary>Remove the partial file when a download fails for good.</summary>
    public bool DeleteFailedFiles { get; set; } = true;
}
