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
    TimeProvider clock,
    NotificationService notifications,
    ILogger<DownloadService> logger)
{
    /// <summary>Adds a playlist entry to the queue and returns the nzo id the client will track it by.</summary>
    public async Task<DownloadItem> EnqueueAsync(
        long itemId, string? category, int priority, string? nameOverride, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var item = await db.Items
            .Include(x => x.Source)
            .FirstOrDefaultAsync(x => x.Id == itemId, ct)
            ?? throw new InvalidOperationException($"Playlist entry {itemId} no longer exists.");

        var settings = await settingsService.GetAsync(ct);

        var download = new DownloadItem
        {
            NzoId = NewNzoId(),
            Name = string.IsNullOrWhiteSpace(nameOverride)
                ? ReleaseFactory.BuildName(item, item.Source)
                : nameOverride.Trim(),
            Category = category?.Trim() ?? string.Empty,
            M3uItemId = item.Id,
            SourceId = item.SourceId,
            StreamUrl = item.StreamUrl,
            Status = DownloadStatus.Queued,
            Priority = priority,
            TotalBytes = SizeEstimator.Estimate(item, settings),
            QueuedAt = clock.GetUtcNow()
        };

        db.Downloads.Add(download);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Queued {Name} as {NzoId}", download.Name, download.NzoId);

        notifications.Notify(Core.Options.NotificationEvent.DownloadQueued, download.Name,
        [
            new("Category", download.Category),
            new("Playlist", item.Source?.Name),
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
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Deletes whatever this download wrote. The completed folder belongs to us entirely
    /// (one folder per release), so removing it recursively is safe.
    /// </summary>
    private void DeleteArtifacts(DownloadItem download)
    {
        try
        {
            if (download.IncompletePath is { Length: > 0 } incomplete)
            {
                if (File.Exists(incomplete)) File.Delete(incomplete);

                var folder = Path.GetDirectoryName(incomplete);
                if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
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
