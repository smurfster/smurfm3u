namespace Smurfm3u.Core.Options;

/// <summary>
/// First-run values read from configuration or environment, used only to seed the database.
/// Once seeded, the UI is the source of truth.
/// </summary>
public class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string AdminUsername { get; set; } = "admin";

    /// <summary>Seeded on first run. Leave blank to have one generated and logged.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Seeded on first run. Leave blank to have one generated.</summary>
    public string? ApiKey { get; set; }
}
