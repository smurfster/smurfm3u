namespace Smurfm3u.Core.Entities;

public enum M3uSourceKind
{
    Remote = 0,
    Local = 1,

    /// <summary>An Xtream Codes panel, read through its player API rather than as a playlist.</summary>
    Xtream = 2
}

public enum MediaKind
{
    Unknown = 0,
    Movie = 1,
    Series = 2
}

public enum RefreshStatus
{
    Never = 0,
    Running = 1,
    Success = 2,
    Failed = 3
}

/// <summary>
/// Lifecycle of a grab. Queued/Downloading/Paused live in the SAB "queue";
/// Completed/Failed/Deleted live in the SAB "history".
/// </summary>
public enum DownloadStatus
{
    Queued = 0,
    Downloading = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Deleted = 5
}

/// <summary>How far one file within a grab has got. A grab with a single file has one of these.</summary>
public enum DownloadFileStatus
{
    Pending = 0,
    Completed = 1,

    /// <summary>The provider no longer has it. The rest of the grab carries on without it.</summary>
    Skipped = 2
}

public enum SearchKind
{
    Search = 0,
    TvSearch = 1,
    MovieSearch = 2,
    Caps = 3
}

/// <summary>
/// Where a recorded search came from. The page shows both, because "what did Sonarr ask for"
/// and "what did I just try" are different questions and mixing them silently is confusing.
/// </summary>
public enum SearchOrigin
{
    /// <summary>A client of the Newznab endpoint: Prowlarr, Sonarr, Radarr.</summary>
    Indexer = 0,

    /// <summary>The Search page in this application.</summary>
    WebUi = 1
}
