using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

public sealed record SpeedLimitState(int EffectiveKibps, bool Paused, string Reason);

/// <summary>
/// Works out the current global rate cap from the schedule plus any manual override,
/// and keeps the shared <see cref="TokenBucket"/> in step with it.
/// </summary>
public class SpeedLimitService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    TimeProvider clock)
{
    /// <summary>The bucket every download draws from.</summary>
    public TokenBucket Global { get; } = new();

    public SpeedLimitState Current { get; private set; } = new(0, false, "unlimited");

    public async Task<SpeedLimitState> RefreshAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var windows = await db.SpeedLimits.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
        var config = await settings.GetAsync(ct);

        var state = Evaluate(windows, config.ManualSpeedLimitKibps, clock.GetLocalNow());
        Current = state;

        Global.SetRate(state.Paused ? -1 : state.EffectiveKibps * 1024d);
        return state;
    }

    /// <summary>
    /// The tightest active constraint wins. A window set to 0 KiB/s means "no downloading
    /// during this window", which is different from the manual override where 0 means unlimited.
    /// </summary>
    public static SpeedLimitState Evaluate(
        IReadOnlyCollection<SpeedLimitWindow> windows, int manualKibps, DateTimeOffset now)
    {
        var active = windows.Where(w => IsActive(w, now)).ToList();

        if (active.Any(w => w.LimitKibps <= 0))
            return new SpeedLimitState(0, true, "paused by schedule");

        int? scheduled = active.Count > 0 ? active.Min(w => w.LimitKibps) : null;

        var candidates = new List<int>();
        if (scheduled is { } s) candidates.Add(s);
        if (manualKibps > 0) candidates.Add(manualKibps);

        if (candidates.Count == 0)
            return new SpeedLimitState(0, false, "unlimited");

        var effective = candidates.Min();
        var reason = scheduled == effective && manualKibps != effective ? "schedule" : "manual limit";
        return new SpeedLimitState(effective, false, reason);
    }

    /// <summary>Windows may wrap past midnight, e.g. 22:00 to 06:00.</summary>
    public static bool IsActive(SpeedLimitWindow window, DateTimeOffset now)
    {
        if (!window.Enabled) return false;
        if (window.StartTime == window.EndTime) return false;

        var time = TimeOnly.FromDateTime(now.DateTime);
        var today = now.DayOfWeek;
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);

        if (window.EndTime > window.StartTime)
            return IsDaySet(window, today) && time >= window.StartTime && time < window.EndTime;

        // Wrapped: the evening half belongs to today, the morning half to yesterday's day bit.
        if (time >= window.StartTime) return IsDaySet(window, today);
        return time < window.EndTime && IsDaySet(window, yesterday);
    }

    public static bool IsDaySet(SpeedLimitWindow window, DayOfWeek day) =>
        (window.DaysOfWeek & (1 << (int)day)) != 0;
}
