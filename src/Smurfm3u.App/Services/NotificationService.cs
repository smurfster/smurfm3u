using Smurfm3u.Core.Options;

namespace Smurfm3u.App.Services;

/// <summary>
/// The one place anything asks for a notification to go out. Callers sit on the download and
/// refresh paths, so nothing here is allowed to block them or to throw at them: a send is
/// handed to the background and every failure ends in the log.
/// </summary>
public class NotificationService(
    SettingsService settingsService,
    SmtpNotifier smtp,
    ILogger<NotificationService> logger)
{
    /// <summary>
    /// Queues a notification if this event is switched on. Returns immediately; the caller is
    /// usually mid-download and has no use for the result.
    /// </summary>
    public void Notify(
        NotificationEvent kind, string subject, IEnumerable<KeyValuePair<string, string?>>? details = null)
    {
        // Materialised now, because the caller's objects may have moved on by the time
        // the background task actually reads them.
        var snapshot = details?.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await settingsService.GetAsync(CancellationToken.None);
                if (!settings.Notifications.ShouldSend(kind)) return;

                var message = NotificationComposer.Compose(kind, subject, snapshot);
                await smtp.SendAsync(settings.Notifications.Smtp, message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // A mail server being down must never turn into a failed download.
                logger.LogWarning(ex, "Could not send the {Event} notification for {Subject}",
                    NotificationComposer.Describe(kind), subject);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Sends a test message with the settings as they are on screen, so the result can be
    /// reported before anything is saved. Unlike <see cref="Notify"/> this one waits and
    /// reports what went wrong, because someone is looking at it.
    /// </summary>
    public async Task<string?> SendTestAsync(NotificationSettings settings, CancellationToken ct = default)
    {
        try
        {
            if (!settings.Smtp.IsUsable)
                return "Fill in the server, the from address and at least one recipient first.";

            var message = NotificationComposer.Compose(
                NotificationEvent.DownloadCompleted,
                "Test notification",
                [new KeyValuePair<string, string?>("Sent by", "Smurfm3u")]);

            await smtp.SendAsync(
                settings.Smtp,
                message with { Subject = "[Smurfm3u] Test notification" },
                ct);

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test notification failed");
            return ex.Message;
        }
    }
}
