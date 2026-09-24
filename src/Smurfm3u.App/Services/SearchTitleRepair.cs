using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Rewrites the stored match keys of entries whose title carries an ampersand.
/// <para>
/// An ampersand used to vanish when a title was reduced to its match key, so
/// "Sherlock &amp; Daughter" was stored as "sherlock daughter". Sonarr spells it out and asks
/// for "Sherlock and Daughter", which made "and" a word the stored key could never contain -
/// so every title with an ampersand in it was unfindable from the one client that matters
/// most, and a season search fell back to near matches with no season pack among them.
/// </para>
/// <para>
/// The keys are spelled out now, but the ones already stored were computed under the old rule.
/// A refresh re-derives most of them; a panel's episodes it does not, because those are only
/// written when they are fetched. So they are corrected here instead, once.
/// </para>
/// </summary>
public class SearchTitleRepair(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<SearchTitleRepair> logger)
{
    /// <summary>
    /// Recorded once the repair has run, so it costs nothing on every later start. Named for
    /// the reason rather than a version, so it is obvious what a stray row is for.
    /// </summary>
    private const string DoneKey = "repair:ampersand-search-titles";

    /// <summary>Rows rewritten per save, so a large catalogue does not build one huge command.</summary>
    private const int BatchSize = 1000;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Settings.AsNoTracking().AnyAsync(x => x.Key == DoneKey, ct))
            return;

        // Only a title with an ampersand in it can have moved, so nothing else is read.
        var items = await RepairAsync(
            db, await db.Items.Where(x => x.Title.Contains("&")).ToListAsync(ct),
            x => x.Title, x => x.SearchTitle, (x, key) => x.SearchTitle = key, ct);

        var series = await RepairAsync(
            db, await db.Series.Where(x => x.Title.Contains("&")).ToListAsync(ct),
            x => x.Title, x => x.SearchTitle, (x, key) => x.SearchTitle = key, ct);

        db.Settings.Add(new AppSetting { Key = DoneKey, Value = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);

        if (items + series > 0)
        {
            logger.LogInformation(
                "Rewrote the match keys of {Items} entries and {Series} series whose title has an ampersand; "
                + "they are findable now by a client that spells it \"and\"",
                items, series);
        }
    }

    private static async Task<int> RepairAsync<T>(
        AppDbContext db,
        List<T> rows,
        Func<T, string> title,
        Func<T, string> currentKey,
        Action<T, string> setKey,
        CancellationToken ct)
    {
        var changed = 0;

        foreach (var row in rows)
        {
            var fresh = ReleaseTitleParser.Normalize(title(row));

            if (fresh == currentKey(row)) continue;

            setKey(row, fresh);

            if (++changed % BatchSize == 0) await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }
}
