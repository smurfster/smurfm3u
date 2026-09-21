using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Applies the retention settings: trims search history and finished downloads so the
/// database does not grow without bound on a busy instance.
/// </summary>
public class MaintenanceWorker(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settingsService,
    TimeProvider clock,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);

        try
        {
            do
            {
                try
                {
                    await PruneAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Deletes history past its retention window. A retention of 0 means keep forever.</summary>
    public async Task<(int Searches, int Downloads)> PruneAsync(CancellationToken ct = default)
    {
        var settings = await settingsService.GetAsync(ct);
        var now = clock.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var searches = 0;
        if (settings.SearchHistoryRetentionDays > 0)
        {
            var cutoff = now.AddDays(-settings.SearchHistoryRetentionDays);
            searches = await db.Searches.Where(x => x.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
        }

        var downloads = 0;
        if (settings.DownloadHistoryRetentionDays > 0)
        {
            var cutoff = now.AddDays(-settings.DownloadHistoryRetentionDays);

            // Only finished rows are eligible; anything still in the queue stays put.
            downloads = await db.Downloads
                .Where(x => x.CompletedAt != null
                            && x.CompletedAt < cutoff
                            && (x.Status == DownloadStatus.Completed
                                || x.Status == DownloadStatus.Failed
                                || x.Status == DownloadStatus.Deleted))
                .ExecuteDeleteAsync(ct);
        }

        if (searches > 0 || downloads > 0)
            logger.LogInformation("Retention removed {Searches} search(es) and {Downloads} download(s)",
                searches, downloads);

        return (searches, downloads);
    }
}
