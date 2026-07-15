using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Valenius.Backend.Data;

namespace Valenius.Backend.Services;

/// <summary>
/// Sends outbound e-mail via MailKit using the SMTP settings stored in ServerSettings.
/// Reads settings fresh from the DB on every send so admin changes take effect immediately.
/// </summary>
public sealed class EmailService(IServiceScopeFactory scopeFactory, ILogger<EmailService> logger)
    : IEmailService
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        => SendAsync(to, subject, htmlBody, [], ct);

    public async Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment> attachments, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = await db.ServerSettings.FirstAsync(ct);

        if (!settings.SmtpEnabled || string.IsNullOrEmpty(settings.SmtpHost))
            throw new InvalidOperationException(
                "SMTP is not configured. Enable it in Admin → Settings → E-mail.");

        var message = new MimeMessage();

        var fromText = string.IsNullOrWhiteSpace(settings.EmailFrom)
            ? "Valenius <noreply@example.com>"
            : settings.EmailFrom;
        message.From.Add(MailboxAddress.Parse(fromText));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        // Build body — use BodyBuilder so we can handle inline images and attachments.
        var builder = new BodyBuilder { HtmlBody = htmlBody };

        foreach (var att in attachments)
        {
            var (mType, mSubtype) = ParseMediaType(att.MediaType);
            var ct2 = new ContentType(mType, mSubtype);

            if (att.IsInline && att.ContentId is not null)
            {
                var part = (MimePart)builder.LinkedResources.Add(att.FileName, att.Data, ct2);
                part.ContentId = att.ContentId;
            }
            else
            {
                builder.Attachments.Add(att.FileName, att.Data, ct2);
            }
        }

        message.Body = builder.ToMessageBody();

        var secureOption = settings.SmtpUseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        using var smtp = new SmtpClient();

        logger.LogDebug("SMTP connecting to {Host}:{Port} (ssl={Ssl})",
            settings.SmtpHost, settings.SmtpPort, settings.SmtpUseSsl);

        await smtp.ConnectAsync(settings.SmtpHost, settings.SmtpPort, secureOption, ct);

        if (!string.IsNullOrEmpty(settings.SmtpUsername))
            await smtp.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword ?? "", ct);

        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(quit: true, ct);

        logger.LogInformation("E-mail sent to {To} — subject: {Subject}", to, subject);
    }

    private static (string Type, string Subtype) ParseMediaType(string mediaType)
    {
        var slash = mediaType.IndexOf('/');
        return slash > 0
            ? (mediaType[..slash], mediaType[(slash + 1)..])
            : (mediaType, "octet-stream");
    }
}
