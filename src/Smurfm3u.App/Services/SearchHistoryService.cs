using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

public sealed record SearchHistoryPage(IReadOnlyList<SearchHistoryEntry> Entries, int TotalCount);

/// <summary>One release a recorded search answered with.</summary>
public sealed record RecordedResult(
    long ItemId, string ReleaseName, string? SourceName, MediaKind Kind, bool StillListed);

/// <summary>Backs the Searches page: what clients asked for, and pruning of what they asked.</summary>
public class SearchHistoryService(
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider clock,
    ILogger<SearchHistoryService> logger)
{
    /// <summary>How many of a query's results are remembered against it.</summary>
    private const int MaxRecordedResults = 200;

    /// <summary>
    /// Writes one answered query to the history.
    /// <para>
    /// Never throws: the history is a convenience, and failing a search because it could not
    /// be logged would trade something that matters for something that does not.
    /// </para>
    /// </summary>
    public async Task RecordAsync(
        SearchHistoryEntry entry, IEnumerable<long> resultIds, CancellationToken ct = default)
    {
        try
        {
            // Bounded: a client asking for a huge page should not write a huge row. Ids of
            // zero are left out - a season pack stands for entries rather than being one, so
            // there is no row for the history page to link back to.
            entry.ResultItemIds = resultIds.Where(x => x > 0).Take(MaxRecordedResults).ToList();
            entry.RequestedAt = clock.GetUtcNow();

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            db.Searches.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record search history");
        }
    }

    public async Task<SearchHistoryPage> GetPageAsync(
        int skip, int take, string? filter = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Searches.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var needle = filter.Trim().ToLowerInvariant();
            query = query.Where(x => x.Query != null && EF.Functions.ILike(x.Query, $"%{needle}%"));
        }

        var total = await query.CountAsync(ct);

        var entries = await query
            .OrderByDescending(x => x.RequestedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

        return new SearchHistoryPage(entries, total);
    }

    /// <summary>
    /// The entries one recorded search answered with, in the order they were sent. Names are
    /// rebuilt from the entry and its playlist rather than stored, so a playlist whose tags
    /// have changed since shows today's name; an entry dropped from every playlist since is
    /// reported as missing rather than silently left out.
    /// </summary>
    public async Task<IReadOnlyList<RecordedResult>> GetResultsAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entry = await db.Searches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entry is null || entry.ResultItemIds.Count == 0) return [];

        var items = await db.Items
            .AsNoTracking()
            .Include(x => x.Source)
            .Where(x => entry.ResultItemIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        return entry.ResultItemIds
            .Select(itemId => items.TryGetValue(itemId, out var item)
                ? new RecordedResult(
                    itemId,
                    // As it was offered: a search by date was answered under dated names.
                    ReleaseFactory.BuildName(item, item.Source, entry.AirDate is not null),
                    item.Source?.Name,
                    item.Kind,
                    item.IsActive)
                : new RecordedResult(itemId, "(no longer in any playlist)", null, MediaKind.Unknown, false))
            .ToList();
    }

    /// <summary>
    /// Deletes entries older than the given number of days. 0 clears everything.
    /// <para>
    /// This is what the retention pass calls on its hourly run. The page does not offer it:
    /// a window the worker already enforces can only ever find what the worker has not got
    /// to yet, which is nothing, and a button that reliably removes nothing reads as broken.
    /// </para>
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(int days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (days <= 0)
            return await db.Searches.ExecuteDeleteAsync(ct);

        var cutoff = clock.GetUtcNow().AddDays(-days);
        return await db.Searches.Where(x => x.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>Deletes the named entries. Ids that name nothing are ignored.</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Materialised so EF parameterises a list rather than closing over the caller's set,
        // which the page mutates while this runs.
        var wanted = ids.ToList();

        return await db.Searches.Where(x => wanted.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Empties the history.</summary>
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Searches.ExecuteDeleteAsync(ct);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Searches.Where(x => x.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}
