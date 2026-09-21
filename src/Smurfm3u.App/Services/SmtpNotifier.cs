using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Smurfm3u.Core.Options;

namespace Smurfm3u.App.Services;

/// <summary>
/// Carries a notification over SMTP. MailKit rather than System.Net.Mail because implicit
/// TLS on port 465 is what most hosted mail providers offer and the built-in client cannot
/// speak it at all.
/// </summary>
public class SmtpNotifier(ILogger<SmtpNotifier> logger)
{
    /// <summary>Nothing here is worth holding a download path open for.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(SmtpSettings smtp, NotificationMessage message, CancellationToken ct = default)
    {
        if (!smtp.IsUsable)
            throw new InvalidOperationException("SMTP is not configured: a host, a from address and a recipient are required.");

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(smtp.FromAddress.Trim()));

        foreach (var recipient in smtp.Recipients())
            mail.To.Add(MailboxAddress.Parse(recipient));

        mail.Subject = message.Subject;
        mail.Body = new TextPart("plain") { Text = message.Body };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        using var client = new SmtpClient();

        // Implicit TLS is encrypted from the first byte (465); everything else offers
        // STARTTLS and we take it when the server advertises it.
        var security = smtp.UseImplicitTls
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(smtp.Host.Trim(), smtp.Port, security, timeout.Token);

        if (!string.IsNullOrWhiteSpace(smtp.Username))
            await client.AuthenticateAsync(smtp.Username.Trim(), smtp.Password ?? string.Empty, timeout.Token);

        await client.SendAsync(mail, timeout.Token);
        await client.DisconnectAsync(quit: true, timeout.Token);

        logger.LogDebug("Sent {Subject} to {Count} recipient(s)", message.Subject, mail.To.Count);
    }
}
