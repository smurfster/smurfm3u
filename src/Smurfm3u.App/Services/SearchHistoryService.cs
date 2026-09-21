using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

public sealed record SearchHistoryPage(IReadOnlyList<SearchHistoryEntry> Entries, int TotalCount);

/// <summary>Backs the Searches page: what clients asked for, and pruning of what they asked.</summary>
public class SearchHistoryService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock)
{
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

    /// <summary>Deletes entries older than the given number of days. 0 clears everything.</summary>
    public async Task<int> DeleteOlderThanAsync(int days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (days <= 0)
            return await db.Searches.ExecuteDeleteAsync(ct);

        var cutoff = clock.GetUtcNow().AddDays(-days);
        return await db.Searches.Where(x => x.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Searches.Where(x => x.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}
