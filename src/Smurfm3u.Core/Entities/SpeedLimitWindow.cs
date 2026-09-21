namespace Smurfm3u.Core.Entities;

/// <summary>
/// A recurring window during which the global download rate is capped.
/// Windows may wrap past midnight (start 22:00, end 06:00); the tightest matching cap wins.
/// </summary>
public class SpeedLimitWindow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Bitmask over <see cref="DayOfWeek"/>, bit 0 = Sunday. 127 = every day.</summary>
    public int DaysOfWeek { get; set; } = 127;

    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>Cap in KiB/s while the window is active. 0 = fully paused.</summary>
    public int LimitKibps { get; set; }
}
