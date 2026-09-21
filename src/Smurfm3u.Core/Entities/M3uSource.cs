namespace Smurfm3u.Core.Entities;

/// <summary>
/// An M3U playlist we ingest. Either a remote URL or a path on a mounted volume.
/// </summary>
public class M3uSource
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public M3uSourceKind Kind { get; set; } = M3uSourceKind.Remote;

    /// <summary>Absolute URL for <see cref="M3uSourceKind.Remote"/>, container path for Local.</summary>
    public string Location { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Quality tag stamped onto every release name from this source, e.g. "WEB-DL".</summary>
    public string QualityTag { get; set; } = "WEB-DL";

    /// <summary>Optional resolution token appended to release names, e.g. "1080p". Blank to omit.</summary>
    public string ResolutionTag { get; set; } = "1080p";

    /// <summary>Release group suffix, e.g. "Smurfm3u" -> "...-Smurfm3u".</summary>
    public string ReleaseGroup { get; set; } = "Smurfm3u";

    public int MaxConcurrentDownloads { get; set; } = 1;

    /// <summary>Seconds to wait after starting a download before starting the next one from this source.</summary>
    public int StartDelaySeconds { get; set; }

    /// <summary>Five-field cron expression. Null/blank disables scheduled refresh.</summary>
    public string? RefreshCron { get; set; } = "0 */6 * * *";

    /// <summary>Per-source speed cap in KiB/s. 0 = unlimited (the global schedule still applies).</summary>
    public int SpeedLimitKibps { get; set; }

    /// <summary>Extra HTTP headers for remote fetch + downloads, one "Name: value" per line.</summary>
    public string? Headers { get; set; }

    public DateTimeOffset? LastRefreshStartedAt { get; set; }
    public DateTimeOffset? LastRefreshCompletedAt { get; set; }
    public RefreshStatus LastRefreshStatus { get; set; } = RefreshStatus.Never;
    public string? LastRefreshError { get; set; }

    public int TotalEntries { get; set; }
    public int VodEntries { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<M3uItem> Items { get; set; } = [];
}
