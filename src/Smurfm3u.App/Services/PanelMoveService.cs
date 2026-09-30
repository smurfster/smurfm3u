using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Xtream;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>What moving a panel's stored addresses changed.</summary>
/// <param name="Entries">Playlist entries now pointing at the new address.</param>
/// <param name="Superseded">
/// Entries left alone because the new address already has an entry of its own - fetched
/// from there before the move - so moving the old one would only duplicate it.
/// </param>
/// <param name="Downloads">Files of unfinished downloads now pointing at the new address.</param>
public sealed record PanelMoveResult(int Entries, int Superseded, int Downloads);

/// <summary>
/// Moves a panel's stored stream addresses when its address or login changes.
/// <para>
/// Every entry keeps the full address it streams from, and a panel's series episodes are only
/// written again when something searches for their series. So when a provider moves to a new
/// domain, a refresh brings the films across but leaves nearly every episode pointing at a
/// server that no longer answers, one search at a time until each series has been asked for.
/// </para>
/// <para>
/// Only the start of each address depends on the panel - the server and the login - and the
/// stream id after it does not, so the new address can be worked out rather than asked for.
/// The entry key, which is a hash of the address, moves with it, so the next fetch recognises
/// each entry rather than adding it again and retiring the old one.
/// </para>
/// </summary>
public class PanelMoveService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PanelMoveService> logger)
{
    private static readonly XtreamStreamKind[] Kinds =
        [XtreamStreamKind.Movie, XtreamStreamKind.Series, XtreamStreamKind.Live];

    /// <summary>
    /// Rewrites every stored address under <paramref name="from"/> to the same stream under
    /// <paramref name="to"/>. Nothing happens when the two produce the same addresses.
    /// </summary>
    public async Task<PanelMoveResult> MoveAsync(
        int sourceId, XtreamCredentials from, XtreamCredentials to, CancellationToken ct = default)
    {
        var moves = Kinds
            .Select(kind => (Old: from.StreamPrefix(kind), New: to.StreamPrefix(kind)))
            .Where(x => x.Old != x.New)
            .ToList();

        if (moves.Count == 0) return new PanelMoveResult(0, 0, 0);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A panel's catalogue runs to a million entries, each rehashed here.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(15));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        int entries = 0, superseded = 0, downloads = 0;

        foreach (var (oldPrefix, newPrefix) in moves)
        {
            // The key is computed exactly as M3uRefreshService.ItemKeyFor computes it: the first
            // 32 hex characters of the SHA-256 of the address's UTF-8 bytes.
            entries += await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Items" AS i
                SET "StreamUrl" = moved.url,
                    "ItemKey" = moved.key
                FROM (
                    SELECT x."Id",
                           {newPrefix} || substr(x."StreamUrl", length({oldPrefix}) + 1) AS url,
                           substr(encode(sha256(convert_to(
                               {newPrefix} || substr(x."StreamUrl", length({oldPrefix}) + 1), 'UTF8')), 'hex'), 1, 32) AS key
                    FROM "Items" AS x
                    WHERE x."SourceId" = {sourceId} AND starts_with(x."StreamUrl", {oldPrefix})
                ) AS moved
                WHERE i."Id" = moved."Id"
                  AND NOT EXISTS (
                      SELECT 1 FROM "Items" AS there
                      WHERE there."SourceId" = {sourceId} AND there."ItemKey" = moved.key)
                """, ct);

            // What is left under the old address already has an entry at the new one.
            superseded += await db.Items
                .Where(x => x.SourceId == sourceId && x.StreamUrl.StartsWith(oldPrefix))
                .CountAsync(ct);

            // A grab waiting in the queue, paused, or failed and waiting for a retry would
            // otherwise ask the old server. A finished one is history and is left as it was.
            downloads += await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "DownloadFiles" AS f
                SET "StreamUrl" = {newPrefix} || substr(f."StreamUrl", length({oldPrefix}) + 1)
                FROM "Downloads" AS d
                WHERE d."Id" = f."DownloadId"
                  AND d."SourceId" = {sourceId}
                  AND d."Status" NOT IN ({(int)DownloadStatus.Completed}, {(int)DownloadStatus.Deleted})
                  AND starts_with(f."StreamUrl", {oldPrefix})
                """, ct);
        }

        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Source {SourceId}: moved {Entries} entries and {Downloads} queued files to the new panel address"
            + "; {Superseded} already had an entry there and were left",
            sourceId, entries, downloads, superseded);

        return new PanelMoveResult(entries, superseded, downloads);
    }
}
