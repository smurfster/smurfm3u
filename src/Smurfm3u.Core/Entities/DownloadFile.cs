namespace Smurfm3u.Core.Entities;

/// <summary>
/// One file within a grab. A single episode or film has exactly one of these; a season pack
/// has one per episode, all landing in the same completed folder.
/// <para>
/// Everything that varies per file lives here rather than on the grab, so the transfer loop
/// has one shape whether it is moving one file or twenty-four.
/// </para>
/// </summary>
public class DownloadFile
{
    public long Id { get; set; }

    public long DownloadId { get; set; }
    public DownloadItem? Download { get; set; }

    /// <summary>Order within the grab; files are transferred in this order.</summary>
    public int Position { get; set; }

    /// <summary>
    /// What this file is written as, without its extension. For a pack this is the episode's
    /// own release name, which is what lets Sonarr import the folder file by file.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Container extension without the dot, e.g. "mkv".</summary>
    public string Extension { get; set; } = "mp4";

    public long? M3uItemId { get; set; }
    public M3uItem? M3uItem { get; set; }

    public string StreamUrl { get; set; } = string.Empty;

    public DownloadFileStatus Status { get; set; } = DownloadFileStatus.Pending;

    /// <summary>Estimated up front, replaced by the length the server declares once it answers.</summary>
    public long TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public string? IncompletePath { get; set; }

    /// <summary>Why this one file was skipped, when it was.</summary>
    public string? FailureMessage { get; set; }
}
