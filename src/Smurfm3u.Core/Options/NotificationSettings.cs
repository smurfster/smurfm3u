using System.Globalization;

namespace Smurfm3u.Core.Options;

/// <summary>Things worth telling someone about. Each can be turned on or off on its own.</summary>
public enum NotificationEvent
{
    DownloadQueued = 0,
    DownloadStarted = 1,
    DownloadCompleted = 2,
    DownloadFailed = 3,
    RefreshFailed = 4
}

/// <summary>
/// One outbound channel. SMTP is the only protocol so far; the settings are shaped so a
/// second one slots in beside it rather than replacing it.
/// </summary>
public class SmtpSettings
{
    public string Host { get; set; } = string.Empty;

    /// <summary>587 for STARTTLS, 465 for implicit TLS, 25 for an unauthenticated relay.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// Implicit TLS, where the connection is encrypted from the first byte. This is what
    /// port 465 expects; 587 and 25 negotiate with STARTTLS instead.
    /// </summary>
    public bool UseImplicitTls { get; set; }

    public string? Username { get; set; }

    /// <summary>Stored as written, because SMTP has to present it. See the README.</summary>
    public string? Password { get; set; }

    public string FromAddress { get; set; } = string.Empty;

    /// <summary>One or more recipients, separated by commas or semicolons.</summary>
    public string ToAddresses { get; set; } = string.Empty;

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Host)
        && Port is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(FromAddress)
        && Recipients().Count > 0;

    /// <summary>Splits the recipient list, tolerating either separator and stray spacing.</summary>
    public IReadOnlyList<string> Recipients() =>
        (ToAddresses ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>Which events go out, and where.</summary>
public class NotificationSettings
{
    public bool Enabled { get; set; }

    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>
    /// Defaults to the two an operator actually wants unprompted. Started and queued are
    /// noisy on a busy instance, so they are opt-in.
    /// </summary>
    public List<NotificationEvent> Events { get; set; } =
        [NotificationEvent.DownloadCompleted, NotificationEvent.DownloadFailed, NotificationEvent.RefreshFailed];

    /// <summary>True when this event should be sent and the channel could actually send it.</summary>
    public bool ShouldSend(NotificationEvent kind) =>
        Enabled && Events.Contains(kind) && Smtp.IsUsable;
}

/// <summary>A composed message, ready for whichever channel carries it.</summary>
public sealed record NotificationMessage(string Subject, string Body);

/// <summary>
/// Turns an event into the text that gets sent. Kept free of any transport so the wording is
/// the same whatever carries it, and so it can be tested without a mail server.
/// </summary>
public static class NotificationComposer
{
    public static string Describe(NotificationEvent kind) => kind switch
    {
        NotificationEvent.DownloadQueued => "Download queued",
        NotificationEvent.DownloadStarted => "Download started",
        NotificationEvent.DownloadCompleted => "Download completed",
        NotificationEvent.DownloadFailed => "Download failed",
        NotificationEvent.RefreshFailed => "Playlist refresh failed",
        _ => "Notification"
    };

    /// <summary>
    /// Subject carries the headline so it reads in a notification list without opening it;
    /// the body carries the detail, one labelled line each, blanks left out.
    /// </summary>
    public static NotificationMessage Compose(
        NotificationEvent kind, string subject, IEnumerable<KeyValuePair<string, string?>>? details = null)
    {
        var headline = Describe(kind);
        var name = string.IsNullOrWhiteSpace(subject) ? "(unnamed)" : subject.Trim();

        var lines = new List<string> { headline, name, string.Empty };

        if (details is not null)
        {
            foreach (var (label, value) in details)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                lines.Add($"{label}: {value.Trim()}");
            }
        }

        return new NotificationMessage($"[Smurfm3u] {headline}: {name}", string.Join('\n', lines).TrimEnd() + "\n");
    }

    /// <summary>Byte counts read better as sizes in a message than as raw numbers.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < units.Length - 1)
        {
            size /= 1024;
            order++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {units[order]}");
    }
}
