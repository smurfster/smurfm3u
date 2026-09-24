using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Core.Models;
using Smurfm3u.Core.Parsing;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Queue and history operations shared by the SABnzbd endpoint and the web UI.
/// </summary>
public class DownloadService(
    IDbContextFactory<AppDbContext> dbFactory,
    DownloadManager manager,
    SettingsService settingsService,
    SeasonPackService seasonPacks,
    TimeProvider clock,
    NotificationService notifications,
    ILogger<DownloadService> logger)
{
    /// <summary>
    /// Queues whatever a search offered under this id: one playlist entry, or a whole season.
    /// Returns the nzo id the client will track it by.
    /// </summary>
    public async Task<DownloadItem> EnqueueAsync(
        string downloadId, string? category, int priority, string? nameOverride, CancellationToken ct = default)
    {
        if (!SeasonPackId.TryParse(downloadId, out var packId))
        {
            return long.TryParse(downloadId, out var itemId)
                ? await EnqueueItemsAsync([itemId], nameOverride, category, priority, ct)
                : throw new InvalidOperationException($"\"{downloadId}\" is not a release this service offered.");
        }

        var settings = await settingsService.GetAsync(ct);

        // Resolved now rather than at search time, so the season is whatever it is today.
        var pack = await seasonPacks.ResolveAsync(packId, settings, ct)
            ?? throw new InvalidOperationException("That season is no longer in the playlist.");

        return await EnqueueItemsAsync(
            [.. pack.Episodes.Select(x => x.Id)],
            string.IsNullOrWhiteSpace(nameOverride) ? pack.Name : nameOverride,
            category, priority, ct);
    }

    /// <summary>
    /// Queues one grab covering these entries, in this order. One entry is an episode or a
    /// film; several are a season pack, which lands as one queue slot writing several files
    /// into one folder.
    /// </summary>
    public async Task<DownloadItem> EnqueueItemsAsync(
        IReadOnlyList<long> itemIds, string? nameOverride, string? category, int priority,
        CancellationToken ct = default)
    {
        if (itemIds.Count == 0)
            throw new InvalidOperationException("That release has nothing in it.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var found = await db.Items
            .Include(x => x.Source)
            .Where(x => itemIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        // Kept in the order they were asked for, which for a pack is episode order.
        var items = itemIds.Select(id => found.GetValueOrDefault(id)).OfType<M3uItem>().ToList();

        if (items.Count == 0)
            throw new InvalidOperationException($"Playlist entry {itemIds[0]} no longer exists.");

        var settings = await settingsService.GetAsync(ct);
        var first = items[0];

        var download = new DownloadItem
        {
            NzoId = NewNzoId(),
            Name = string.IsNullOrWhiteSpace(nameOverride)
                ? ReleaseFactory.BuildName(first, first.Source)
                : nameOverride.Trim(),
            Category = category?.Trim() ?? string.Empty,
            SourceId = first.SourceId,
            Status = DownloadStatus.Queued,
            Priority = priority,
            QueuedAt = clock.GetUtcNow()
        };

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            download.Files.Add(new DownloadFile
            {
                Position = i,
                // One file keeps the grab's own name, so a single episode lands on disk
                // exactly as it always has. A pack names each file after its episode.
                Name = items.Count == 1
                    ? download.Name
                    : ReleaseFactory.BuildName(item, item.Source),
                Extension = string.IsNullOrWhiteSpace(item.Extension) ? "mp4" : item.Extension,
                M3uItemId = item.Id,
                StreamUrl = item.StreamUrl,
                TotalBytes = SizeEstimator.Estimate(item, settings)
            });
        }

        download.TotalBytes = download.Files.Sum(x => x.TotalBytes);

        db.Downloads.Add(download);
        await db.SaveChangesAsync(ct);

        if (items.Count > 1)
            logger.LogInformation("Queued {Name} as {NzoId}: {Count} episodes in one grab",
                download.Name, download.NzoId, items.Count);
        else
            logger.LogInformation("Queued {Name} as {NzoId}", download.Name, download.NzoId);

        if (items.Count != itemIds.Count)
            logger.LogWarning("{Name}: {Missing} of {Total} entries have gone since the search",
                download.Name, itemIds.Count - items.Count, itemIds.Count);

        notifications.Notify(Core.Options.NotificationEvent.DownloadQueued, download.Name,
        [
            new("Category", download.Category),
            new("Playlist", first.Source?.Name),
            new("Files", items.Count > 1 ? items.Count.ToString() : null),
            new("Size", Core.Options.NotificationComposer.FormatBytes(download.TotalBytes))
        ]);

        return download;
    }

    public async Task<DownloadItem?> FindAsync(string nzoId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Downloads.AsNoTracking().FirstOrDefaultAsync(x => x.NzoId == nzoId, ct);
    }

    public async Task<bool> PauseAsync(string nzoId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var download = await db.Downloads.FirstOrDefaultAsync(x => x.NzoId == nzoId, ct);
        if (download is null || !download.IsActive) return false;

        // A running transfer stops itself and records Paused; a queued one just changes state.
        if (!manager.Pause(download.Id))
        {
            download.Status = DownloadStatus.Paused;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<bool> ResumeAsync(string nzoId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var download = await db.Downloads.FirstOrDefaultAsync(x => x.NzoId == nzoId, ct);
        if (download is null || download.Status != DownloadStatus.Paused) return false;

        download.Status = DownloadStatus.Queued;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Pauses or resumes everything currently in the queue.</summary>
    public async Task<int> SetAllPausedAsync(bool paused, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var from = paused ? DownloadStatus.Queued : DownloadStatus.Paused;
        var to = paused ? DownloadStatus.Paused : DownloadStatus.Queued;

        var changed = await db.Downloads
            .Where(x => x.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, to), ct);

        if (paused)
        {
            foreach (var active in manager.Snapshot())
                manager.Pause(active.Id);
        }

        return changed;
    }

    /// <summary>Removes a queued or running download. Marked Deleted so history stays honest.</summary>
    public async Task<bool> RemoveFromQueueAsync(string nzoId, bool deleteFiles, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var download = await db.Downloads.FirstOrDefaultAsync(x => x.NzoId == nzoId, ct);
        if (download is null || !download.IsActive) return false;

        manager.Cancel(download.Id);

        download.Status = DownloadStatus.Deleted;
        download.CompletedAt = clock.GetUtcNow();
        download.BytesPerSecond = 0;
        await db.SaveChangesAsync(ct);

        if (deleteFiles)
            DeleteArtifacts(download);

        logger.LogInformation("Removed {Name} from the queue", download.Name);
        return true;
    }

    /// <summary>Removes everything currently in the queue, as SABnzbd's "delete all" does.</summary>
    public async Task<int> RemoveAllFromQueueAsync(bool deleteFiles, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var active = await db.Downloads
            .Where(x => x.Status == DownloadStatus.Queued
                        || x.Status == DownloadStatus.Downloading
                        || x.Status == DownloadStatus.Paused)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();

        foreach (var download in active)
        {
            manager.Cancel(download.Id);
            download.Status = DownloadStatus.Deleted;
            download.CompletedAt = now;
            download.BytesPerSecond = 0;
        }

        await db.SaveChangesAsync(ct);

        if (deleteFiles)
        {
            foreach (var download in active)
                DeleteArtifacts(download);
        }

        logger.LogInformation("Removed {Count} download(s) from the queue", active.Count);
        return active.Count;
    }

    /// <summary>Deletes a finished item from history, optionally taking the downloaded file with it.</summary>
    public async Task<bool> RemoveFromHistoryAsync(string nzoId, bool deleteFiles, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var download = await db.Downloads.FirstOrDefaultAsync(x => x.NzoId == nzoId, ct);
        if (download is null || download.IsActive) return false;

        if (deleteFiles)
            DeleteArtifacts(download);

        db.Downloads.Remove(download);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Clears every finished item. Used by the history page and SABnzbd's purge.</summary>
    public async Task<int> PurgeHistoryAsync(bool deleteFiles, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var finished = await db.Downloads
            .Where(x => x.Status == DownloadStatus.Completed
                        || x.Status == DownloadStatus.Failed
                        || x.Status == DownloadStatus.Deleted)
            .ToListAsync(ct);

        if (deleteFiles)
        {
            foreach (var download in finished)
                DeleteArtifacts(download);
        }

        db.Downloads.RemoveRange(finished);
        await db.SaveChangesAsync(ct);
        return finished.Count;
    }

    /// <summary>Requeues a failed item for another go.</summary>
    public async Task<bool> RetryAsync(string nzoId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var download = await db.Downloads.FirstOrDefaultAsync(x => x.NzoId == nzoId, ct);
        if (download is null || download.IsActive) return false;

        download.Status = DownloadStatus.Queued;
        download.AttemptCount = 0;
        download.FailureMessage = null;
        download.CompletedAt = null;
        download.CompletedPath = null;

        // Files given up on are offered another go, because asking for a retry is asking for
        // exactly that. Ones already transferred keep their status and are not fetched again.
        await db.DownloadFiles
            .Where(x => x.DownloadId == download.Id && x.Status == DownloadFileStatus.Skipped)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, DownloadFileStatus.Pending)
                .SetProperty(x => x.FailureMessage, (string?)null), ct);

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Deletes whatever this download wrote. Both folders belong to us entirely - one per
    /// grab while it runs, one per release once it finishes - so removing them recursively
    /// is safe.
    /// </summary>
    private void DeleteArtifacts(DownloadItem download)
    {
        try
        {
            // A grab writes into a folder of its own. Rows written before packs existed point
            // at the single file inside it instead, so both shapes are handled.
            if (download.IncompletePath is { Length: > 0 } incomplete)
            {
                if (Directory.Exists(incomplete))
                {
                    Directory.Delete(incomplete, recursive: true);
                }
                else
                {
                    if (File.Exists(incomplete)) File.Delete(incomplete);

                    var folder = Path.GetDirectoryName(incomplete);
                    if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
            }

            if (download.CompletedPath is { Length: > 0 } completed && Directory.Exists(completed))
                Directory.Delete(completed, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete files for {Name}", download.Name);
        }
    }

    /// <summary>SABnzbd's id shape; the *arr apps only require that it is stable and unique.</summary>
    private static string NewNzoId() =>
        "SABnzbd_nzo_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
}
