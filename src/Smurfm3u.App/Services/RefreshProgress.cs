using System.Collections.Concurrent;

namespace Smurfm3u.App.Services;

/// <summary>
/// Where a refresh has got to, for the page watching it.
/// </summary>
/// <param name="Phase">What it is doing now, e.g. "films" or "series".</param>
/// <param name="Done">Units finished. What a unit is depends on the phase.</param>
/// <param name="Total">
/// Units expected, or null when there is honestly no way to know: a provider that streams a
/// playlist without declaring a length gives nothing to divide by, and a made-up denominator
/// is worse than a plain count.
/// </param>
public sealed record RefreshStage(string Phase, long Done, long? Total, DateTimeOffset StartedAt)
{
    /// <summary>How far through, or null when the total is unknown.</summary>
    public int? Percent => Total is > 0 ? (int)Math.Clamp(Done * 100 / Total.Value, 0, 100) : null;

    /// <summary>
    /// What is left, at the rate this phase has managed so far. Null until there is enough of
    /// a rate to extrapolate from, because a guess made from three seconds of work is noise.
    /// </summary>
    public TimeSpan? Remaining(DateTimeOffset now)
    {
        if (Total is not > 0 || Done <= 0) return null;

        var elapsed = now - StartedAt;
        if (elapsed < TimeSpan.FromSeconds(5)) return null;

        var perUnit = elapsed / Done;
        return perUnit * Math.Max(0, Total.Value - Done);
    }
}

/// <summary>
/// Live progress for the refreshes running right now, in memory and keyed by source. Shaped
/// like <see cref="DownloadManager"/> for the same reason: the page wants to watch something
/// that moves several times a second, and the database is not the place to put that.
/// </summary>
public sealed class RefreshProgress(TimeProvider clock)
{
    private readonly ConcurrentDictionary<int, RefreshStage> stages = new();

    /// <summary>Starts a phase, or replaces the one before it. Done resets with the phase.</summary>
    public void Begin(int sourceId, string phase, long? total = null) =>
        stages[sourceId] = new RefreshStage(phase, 0, total, clock.GetUtcNow());

    /// <summary>
    /// Moves the current phase on. Ignored when no phase has begun, so a caller reporting
    /// into a refresh that has already finished cannot resurrect it on the page.
    /// </summary>
    public void Report(int sourceId, long done, long? total = null)
    {
        if (!stages.TryGetValue(sourceId, out var stage)) return;

        stages[sourceId] = stage with { Done = done, Total = total ?? stage.Total };
    }

    public void Finish(int sourceId) => stages.TryRemove(sourceId, out _);

    /// <summary>Null once it has finished, which is the page's cue to go back to the counts.</summary>
    public RefreshStage? For(int sourceId) => stages.GetValueOrDefault(sourceId);
}
