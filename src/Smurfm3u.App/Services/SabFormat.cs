using System.Globalization;

namespace Smurfm3u.App.Services;

/// <summary>
/// SABnzbd's API reports numbers as pre-formatted strings, and the *arr clients parse them
/// back out, so the exact shapes here matter more than they look.
/// </summary>
public static class SabFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Human size, e.g. "1.2 GB".</summary>
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 B";

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {Units[unit]}");
    }

    /// <summary>Megabytes as a decimal string; the clients read this as the real size.</summary>
    public static string Megabytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d:0.00}");

    /// <summary>Parsed by the clients as a TimeSpan, so it must stay "H:MM:SS" or "D:HH:MM:SS".</summary>
    public static string TimeLeft(long remainingBytes, long bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond <= 0) return "0:00:00";

        var seconds = remainingBytes / (double)bytesPerSecond;
        if (seconds > TimeSpan.FromDays(99).TotalSeconds) return "99:00:00:00";

        var span = TimeSpan.FromSeconds(seconds);

        return span.Days > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{span.Days}:{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{span.Hours}:{span.Minutes:D2}:{span.Seconds:D2}");
    }

    public static string Percentage(long downloaded, long total) =>
        total <= 0
            ? "0"
            : Math.Clamp((int)(downloaded * 100 / total), 0, 100).ToString(CultureInfo.InvariantCulture);

    /// <summary>Rate as SAB writes it in the queue, e.g. "1.2 M".</summary>
    public static string Speed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0";

        double value = bytesPerSecond;
        string[] units = ["", "K", "M", "G"];
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}").TrimEnd();
    }

    public static string KiloBytesPerSecond(long bytesPerSecond) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1024d:0.00}");
}
