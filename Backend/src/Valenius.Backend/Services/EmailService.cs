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
    private readonly Func<SmtpClient> _createClient = static () => new SmtpClient();

    // Test-only construction seam for a fixture-local trust root. Production DI
    // uses the public constructor and MailKit's normal certificate validation.
    internal EmailService(IServiceScopeFactory scopeFactory, ILogger<EmailService> logger,
        Func<SmtpClient> createClient) : this(scopeFactory, logger) => _createClient = createClient;

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
            : SecureSocketOptions.StartTls;

        using var smtp = _createClient();

        logger.LogDebug("SMTP connecting to {Host}:{Port} (security={Security})",
            settings.SmtpHost, settings.SmtpPort, secureOption);

        try
        {
            await smtp.ConnectAsync(settings.SmtpHost, settings.SmtpPort, secureOption, ct);
        }
        catch (NotSupportedException ex) when (!settings.SmtpUseSsl)
        {
            throw new InvalidOperationException(
                "SMTP requires STARTTLS. Configure a TLS-capable relay, or use implicit TLS with its matching port. No e-mail was sent.", ex);
        }

        if (!smtp.IsSecure)
            throw new InvalidOperationException("SMTP encryption is required. No e-mail was sent.");

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
