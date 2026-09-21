using Cronos;
using Microsoft.EntityFrameworkCore;
using Smurfm3u.Core.Entities;
using Smurfm3u.Data;

namespace Smurfm3u.App.Services;

/// <summary>
/// Runs each enabled source on its own cron schedule. Checks once a minute, which is the
/// finest granularity a five-field cron expression can express anyway.
/// </summary>
public class RefreshScheduler(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider clock,
    ILogger<RefreshScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, clock);

        try
        {
            do
            {
                try
                {
                    await RunDueSourcesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Scheduled refresh pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunDueSourcesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sources = await db.Sources
            .AsNoTracking()
            .Where(x => x.Enabled && x.RefreshCron != null && x.RefreshCron != "")
            .ToListAsync(ct);

        var now = clock.GetUtcNow();

        foreach (var source in sources)
        {
            if (source.LastRefreshStatus == RefreshStatus.Running) continue;
            if (!IsDue(source, now, logger)) continue;

            logger.LogInformation("Cron schedule triggered a refresh of {Source}", source.Name);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var refresher = scope.ServiceProvider.GetRequiredService<M3uRefreshService>();
                await refresher.RefreshAsync(source.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RefreshAsync already recorded the failure against the source.
                logger.LogWarning(ex, "Scheduled refresh of {Source} failed", source.Name);
            }
        }
    }

    /// <summary>
    /// A source is due when the next occurrence after its last run has passed.
    /// A source that has never run is due immediately, so adding one refreshes it right away.
    /// </summary>
    public static bool IsDue(M3uSource source, DateTimeOffset now, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(source.RefreshCron)) return false;

        var anchor = source.LastRefreshStartedAt;
        if (anchor is null) return true;

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(source.RefreshCron, CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            logger?.LogWarning(ex, "Source {Source} has an invalid cron expression: {Cron}",
                source.Name, source.RefreshCron);
            return false;
        }

        var next = expression.GetNextOccurrence(anchor.Value.UtcDateTime, TimeZoneInfo.Utc);
        return next is not null && next <= now.UtcDateTime;
    }
}
