using Microsoft.Extensions.Logging;

namespace Smurfm3u.Core.Diagnostics;

/// <summary>One line as it was written, kept only in memory.</summary>
/// <param name="Sequence">
/// Monotonic, so the page has a stable key for a line and can tell what it has already shown.
/// Two lines written in the same millisecond are otherwise indistinguishable.
/// </param>
public sealed record LogEntry(
    long Sequence,
    DateTimeOffset At,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception)
{
    /// <summary>
    /// The class that wrote it, without its namespace. Full categories are long enough to
    /// push the message off the side of the page, and the namespace is the same every time.
    /// </summary>
    public string Source
    {
        get
        {
            var cut = Category.LastIndexOf('.');
            return cut >= 0 && cut < Category.Length - 1 ? Category[(cut + 1)..] : Category;
        }
    }

    /// <summary>Whether this line belongs in a view filtered to a level and a search term.</summary>
    public bool Matches(LogLevel minimum, string? text)
    {
        if (Level < minimum) return false;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var needle = text.Trim();

        return Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || (Exception?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

/// <summary>
/// The last few hundred log lines, and a signal when another arrives. Everything still goes to
/// the console as it always did; this is a second copy so the web UI can show what happened
/// without anyone reaching for docker logs.
/// </summary>
/// <remarks>
/// Deliberately bounded and deliberately not persisted. A log that grows without limit is a
/// memory leak with a nice name, and one written to the database would have every refresh
/// writing rows about writing rows.
/// </remarks>
public sealed class LogRing(int capacity = 1000)
{
    private readonly Queue<LogEntry> entries = new();
    private readonly Lock gate = new();

    private long sequence;

    public int Capacity { get; } = capacity > 0 ? capacity : 1000;

    /// <summary>Raised once per line, on whichever thread wrote it.</summary>
    public event Action<LogEntry>? Written;

    public void Add(LogLevel level, string category, string message, string? exception, DateTimeOffset at)
    {
        LogEntry entry;

        lock (gate)
        {
            entry = new LogEntry(++sequence, at, level, category, message, exception);
            entries.Enqueue(entry);

            while (entries.Count > Capacity) entries.Dequeue();
        }

        // Outside the lock: a subscriber that blocks would otherwise stall everything that logs.
        Written?.Invoke(entry);
    }

    /// <summary>Oldest first. A copy, so a page can walk it while the service keeps writing.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (gate) return [.. entries];
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
    }
}
