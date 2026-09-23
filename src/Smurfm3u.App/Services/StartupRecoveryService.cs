using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>What one recovery pass put right. Reported once, in the startup log.</summary>
public sealed record RecoveryReport(int Requeued, int Sources, int Trimmed, int Orphans)
{
    public bool IsClean => Requeued == 0 && Sources == 0 && Trimmed == 0 && Orphans == 0;
}

/// <summary>
/// Puts the database and the incomplete directory back into a consistent state after the
/// process stopped without being able to tidy up: a crash, a container kill, a host reboot.
/// Runs once at startup before anything is served, so no client ever sees the stale state.
/// Every step survives its own failure, because a half-mounted volume must not stop the
/// service booting.
/// </summary>
public class StartupRecoveryService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    TimeProvider clock,
    ILogger<StartupRecoveryService> logger)
{
    /// <summary>Incomplete folders are named after the nzo id that owns them.</summary>
    private const string NzoFolderPrefix = "SABnzbd_nzo_";

    public async Task<RecoveryReport> RecoverAsync(CancellationToken ct = default)
    {
        var report = new RecoveryReport(
            await RequeueInterruptedDownloadsAsync(ct),
            await ClearInterruptedRefreshesAsync(ct),
            await TrimPartialFilesAsync(ct),
            await RemoveOrphanedPartialsAsync(ct));

        if (!report.IsClean)
        {
            logger.LogInformation(
                "Recovered from an unclean shutdown: requeued {Requeued} download(s), reset {Sources} source(s), "
                + "trimmed {Trimmed} partial file(s), removed {Orphans} orphaned folder(s)",
                report.Requeued, report.Sources, report.Trimmed, report.Orphans);
        }

        return report;
    }

    /// <summary>
    /// Nothing is running yet, so a row still marked Downloading belongs to the dead process.
    /// Put it back in the queue; the partial file on disk is what lets it carry on.
    /// </summary>
    private async Task<int> RequeueInterruptedDownloadsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var requeued = await db.Downloads
                .Where(x => x.Status == DownloadStatus.Downloading)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, DownloadStatus.Queued)
                    .SetProperty(x => x.BytesPerSecond, 0L), ct);

            // A paused row keeps its status, but not a transfer rate that died with the process.
            await db.Downloads
                .Where(x => x.Status == DownloadStatus.Paused && x.BytesPerSecond != 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.BytesPerSecond, 0L), ct);

            return requeued;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not requeue interrupted downloads");
            return 0;
        }
    }

    /// <summary>
    /// The scheduler skips a source that is already refreshing, so one left marked Running by
    /// a crash would never be picked up again. The cron anchor is deliberately left alone, so
    /// the source refreshes at its next scheduled time rather than on every boot.
    /// </summary>
    private async Task<int> ClearInterruptedRefreshesAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var finishedAt = (DateTimeOffset?)clock.GetUtcNow();

            return await db.Sources
                .Where(x => x.LastRefreshStatus == RefreshStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LastRefreshStatus, RefreshStatus.Failed)
                    .SetProperty(x => x.LastRefreshError, "Interrupted by a restart")
                    .SetProperty(x => x.LastRefreshCompletedAt, finishedAt), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not reset sources left mid-refresh");
            return 0;
        }
    }

    /// <summary>
    /// A partial file can be longer than the progress we last recorded, because its tail was
    /// written but may not have reached the disk before the power went. Cutting it back to the
    /// last checkpoint means a resume can never splice unwritten bytes into the middle of a
    /// file and then call the result complete.
    /// </summary>
    private async Task<int> TrimPartialFilesAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Per file, not per grab: a pack's recorded byte count is the total across its
            // episodes, and trimming one file back to that would truncate the wrong thing.
            var partials = await db.DownloadFiles
                .AsNoTracking()
                .Where(x => x.IncompletePath != null
                            && x.Status == DownloadFileStatus.Pending
                            && (x.Download!.Status == DownloadStatus.Queued
                                || x.Download.Status == DownloadStatus.Paused))
                .Select(x => new { x.Id, x.IncompletePath, x.DownloadedBytes })
                .ToListAsync(ct);

            var trimmed = 0;

            foreach (var partial in partials)
            {
                var path = partial.IncompletePath!;
                if (!File.Exists(path)) continue;

                var length = new FileInfo(path).Length;
                if (length == partial.DownloadedBytes) continue;

                if (length > partial.DownloadedBytes)
                {
                    try
                    {
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                        file.SetLength(partial.DownloadedBytes);
                        trimmed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Could not trim the partial file {Path}", path);
                    }
                }
                else
                {
                    // Shorter than recorded: the file is the truth and the row is stale.
                    var onDisk = length;
                    await db.DownloadFiles
                        .Where(x => x.Id == partial.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.DownloadedBytes, onDisk), ct);
                }
            }

            return trimmed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not reconcile partial files");
            return 0;
        }
    }

    /// <summary>
    /// Removes incomplete folders that no download will ever resume: a grab deleted while the
    /// process was down, or one that finished and died before it could clean up after itself.
    /// </summary>
    private async Task<int> RemoveOrphanedPartialsAsync(CancellationToken ct)
    {
        try
        {
            var settings = await settingsService.GetAsync(ct);
            if (!Directory.Exists(settings.IncompletePath)) return 0;

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var live = await db.Downloads
                .AsNoTracking()
                .Where(x => x.Status == DownloadStatus.Queued
                            || x.Status == DownloadStatus.Downloading
                            || x.Status == DownloadStatus.Paused)
                .Select(x => x.NzoId)
                .ToListAsync(ct);

            var keep = live.ToHashSet(StringComparer.Ordinal);
            var removed = 0;

            foreach (var folder in Directory.EnumerateDirectories(settings.IncompletePath))
            {
                var name = Path.GetFileName(folder);

                // Only folders we created are touched; anything else in there is not ours.
                if (!name.StartsWith(NzoFolderPrefix, StringComparison.Ordinal)) continue;
                if (keep.Contains(name)) continue;

                try
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not remove the orphaned folder {Path}", folder);
                }
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove orphaned partial downloads");
            return 0;
        }
    }
}
