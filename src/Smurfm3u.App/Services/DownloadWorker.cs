using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Dispatches queued downloads, honouring the global concurrency ceiling, each source's own
/// concurrency cap, and the configured pause between consecutive starts from one source.
/// </summary>
public class DownloadWorker(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    DownloadManager manager,
    SettingsService settingsService,
    SpeedLimitService speedLimits,
    TimeProvider clock,
    ILogger<DownloadWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Last time a download was started for a source, for the per-source start delay.</summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastStart = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, clock);

        try
        {
            do
            {
                try
                {
                    await speedLimits.RefreshAsync(stoppingToken);
                    await DispatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The dispatch loop must survive anything a single bad row can throw.
                    logger.LogError(ex, "Download dispatch loop failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        if (speedLimits.Current.Paused)
            return;

        var settings = await settingsService.GetAsync(ct);

        var freeSlots = settings.MaxConcurrentDownloads - manager.Count;
        if (freeSlots <= 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.Downloads
            .AsNoTracking()
            .Where(x => x.Status == DownloadStatus.Queued)
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.QueuedAt)
            .Take(freeSlots * 8)
            .Select(x => new { x.Id, x.SourceId })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        // Source settings are read once per pass rather than per candidate.
        var sourceIds = candidates.Where(x => x.SourceId is not null).Select(x => x.SourceId!.Value).Distinct();
        var sources = await db.Sources
            .AsNoTracking()
            .Where(x => sourceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var now = clock.GetUtcNow();

        foreach (var candidate in candidates)
        {
            if (freeSlots <= 0) break;
            if (manager.Get(candidate.Id) is not null) continue;

            if (candidate.SourceId is { } sourceId && sources.TryGetValue(sourceId, out var source))
            {
                if (manager.CountForSource(sourceId) >= Math.Max(1, source.MaxConcurrentDownloads))
                    continue;

                if (source.StartDelaySeconds > 0
                    && _lastStart.TryGetValue(sourceId, out var last)
                    && now - last < TimeSpan.FromSeconds(source.StartDelaySeconds))
                    continue;

                _lastStart[sourceId] = now;
            }

            if (!await TryClaimAsync(db, candidate.Id, ct))
                continue;

            Start(candidate.Id, candidate.SourceId, ct);
            freeSlots--;
        }
    }

    /// <summary>
    /// Moves the row from Queued to Downloading in one conditional update, so a second
    /// dispatch pass can never start the same download twice.
    /// </summary>
    private static async Task<bool> TryClaimAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var claimed = await db.Downloads
            .Where(x => x.Id == id && x.Status == DownloadStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DownloadStatus.Downloading), ct);

        return claimed == 1;
    }

    private void Start(long downloadId, int? sourceId, CancellationToken stoppingToken)
    {
        var handle = manager.Register(downloadId, sourceId);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var downloader = scope.ServiceProvider.GetRequiredService<FileDownloader>();
                await downloader.RunAsync(downloadId, handle, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled failure running download {DownloadId}", downloadId);
            }
            finally
            {
                manager.Unregister(downloadId);
            }
        }, CancellationToken.None);
    }
}
