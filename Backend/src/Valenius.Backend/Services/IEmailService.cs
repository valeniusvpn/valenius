namespace Valenius.Backend.Services;

/// <summary>A file attached to or embedded inside an outgoing e-mail.</summary>
/// <param name="FileName">Name shown in the attachment or used as the src filename.</param>
/// <param name="Data">Raw file bytes.</param>
/// <param name="MediaType">Full MIME type, e.g. "image/png" or "text/plain".</param>
/// <param name="IsInline">
///     When true the attachment is embedded as a linked resource (cid: reference).
///     Set <paramref name="ContentId"/> to the ID used in the HTML &lt;img src="cid:…"&gt;.
/// </param>
/// <param name="ContentId">Content-Id for inline images (ignored when IsInline=false).</param>
public sealed record EmailAttachment(
    string  FileName,
    byte[]  Data,
    string  MediaType,
    bool    IsInline  = false,
    string? ContentId = null);

public interface IEmailService
{
    /// <summary>
    /// Sends an HTML e-mail with no attachments.
    /// Throws <see cref="InvalidOperationException"/> when SMTP is not configured.
    /// </summary>
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);

    /// <summary>
    /// Sends an HTML e-mail with optional attachments and inline images.
    /// </summary>
    Task SendAsync(string to, string subject, string htmlBody,
                   IReadOnlyList<EmailAttachment> attachments,
                   CancellationToken ct = default);
}
