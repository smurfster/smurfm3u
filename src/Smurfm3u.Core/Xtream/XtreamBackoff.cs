using System.Globalization;

namespace Smurfm3u.Core.Xtream;

/// <summary>
/// How long to leave a panel alone after it has said no.
/// <para>
/// A panel that answers 429 is not broken and the series it refused is not missing: it is
/// asking to be asked more slowly. Treating that as a failure loses a series that is really
/// there, which is why the wait is worth getting right rather than giving up.
/// </para>
/// </summary>
public static class XtreamBackoff
{
    /// <summary>Longer than this and a refresh is hanging rather than waiting.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(2);

    /// <summary>Statuses worth trying again. Everything else is the panel meaning it.</summary>
    public static bool IsWorthRetrying(int statusCode) =>
        statusCode is 429 or 502 or 503 or 504;

    /// <summary>
    /// The panel's own Retry-After when it sent one, because it knows its limits better than
    /// any guess here; otherwise a doubling wait, which is the guess.
    /// </summary>
    /// <param name="attempt">1 for the first retry.</param>
    /// <param name="retryAfter">The header as sent: either seconds, or an HTTP date.</param>
    public static TimeSpan For(int attempt, string? retryAfter, DateTimeOffset now)
    {
        if (Requested(retryAfter, now) is { } asked)
            return Clamp(asked);

        // 2s, 4s, 8s, 16s... A panel that is counting requests per minute needs the later
        // waits to be long enough to cross into the next window.
        var doubling = TimeSpan.FromSeconds(Math.Pow(2, Math.Max(1, attempt)));
        return Clamp(doubling);
    }

    /// <summary>What the header asked for, or null when there was none or it made no sense.</summary>
    private static TimeSpan? Requested(string? retryAfter, DateTimeOffset now)
    {
        var value = (retryAfter ?? string.Empty).Trim();
        if (value.Length == 0) return null;

        // The common form: a number of seconds.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

        // The other permitted form: the date it will be willing again.
        if (DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
        {
            var wait = when - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > Longest ? Longest : value;
}
