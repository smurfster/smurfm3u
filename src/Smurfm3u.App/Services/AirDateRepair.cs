using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Gives episodes stored before air dates were read the date their name already carries.
/// <para>
/// A panel's episodes are only rewritten when they are fetched again, and a panel that is
/// refusing requests may not allow that for a long time - while the date Sonarr asks a daily
/// show by is sitting in the stored name the whole time: "EastEnders (1985) - S42E146 - 14/09/2026".
/// So it is read out of there instead, once.
/// </para>
/// </summary>
public class AirDateRepair(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<AirDateRepair> logger)
{
    /// <summary>Recorded once the repair has run, so it costs nothing on every later start.</summary>
    private const string DoneKey = "repair:episode-air-dates";

    /// <summary>Rows rewritten per save, so a large catalogue does not build one huge command.</summary>
    private const int BatchSize = 1000;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Settings.AsNoTracking().AnyAsync(x => x.Key == DoneKey, ct))
            return;

        // Only a name with something date-shaped in it can have one, so nothing else is read.
        var candidates = await db.Items
            .Where(x => x.Kind == MediaKind.Series && x.AirDate == null
                        && Regex.IsMatch(x.RawTitle, @"\d[-./]\d{1,2}[-./]\d"))
            .ToListAsync(ct);

        var dated = 0;

        foreach (var item in candidates)
        {
            // The episode's own name first, as a fetch reads it; the whole name if that has none.
            if ((AirDates.Find(item.EpisodeTitle) ?? AirDates.Find(item.RawTitle)) is not { } found) continue;

            item.AirDate = found.Date;

            if (++dated % BatchSize == 0) await db.SaveChangesAsync(ct);
        }

        db.Settings.Add(new AppSetting { Key = DoneKey, Value = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);

        if (dated > 0)
            logger.LogInformation("Read the air date of {Count} stored episodes out of their names", dated);
    }
}
