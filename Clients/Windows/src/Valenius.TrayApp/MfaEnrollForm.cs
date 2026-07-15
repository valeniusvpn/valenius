using System.Drawing;
using QRCoder;

namespace Valenius.TrayApp;

/// <summary>
/// Modal dialog for per-client TOTP enrollment: renders the otpauth QR, shows the secret
/// for manual entry, and collects the confirmation code. The caller sends the code to the
/// service (MfaEnrollConfirm) and shows the result via <see cref="SetError"/>.
/// </summary>
internal sealed class MfaEnrollForm : Form
{
    private readonly TextBox _code;
    private readonly Label   _error;

    public string EnteredCode => _code.Text.Trim();

    public MfaEnrollForm(string otpAuthUri)
    {
        Text            = "Set up two-factor authentication";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        ClientSize      = new Size(360, 470);
        BackColor       = Color.FromArgb(22, 32, 48);
        ForeColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);
        // Scale the absolutely-positioned controls with the monitor DPI (PerMonitorV2).
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode       = AutoScaleMode.Dpi;

        var intro = new Label
        {
            Text      = "Scan this with your authenticator app (Google Authenticator, Microsoft Authenticator, etc.), then enter the 6-digit code to finish setup.",
            Location  = new Point(20, 12),
            Size      = new Size(320, 52),
            TextAlign = ContentAlignment.TopCenter
        };

        var qr = new PictureBox
        {
            Location = new Point(80, 70),
            Size     = new Size(200, 200),
            SizeMode = PictureBoxSizeMode.StretchImage,
            BackColor = Color.White,
            Image    = RenderQr(otpAuthUri)
        };

        var secret = ExtractSecret(otpAuthUri);
        var secretLabel = new Label
        {
            Text      = secret is null ? "" : $"Manual key: {secret}",
            Location  = new Point(20, 278),
            Size      = new Size(320, 20),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(180, 198, 222),
            Font      = new Font("Consolas", 8f)
        };

        _code = new TextBox
        {
            Location  = new Point(110, 306),
            Size      = new Size(140, 34),
            MaxLength = 6,
            TextAlign = HorizontalAlignment.Center,
            Font      = new Font("Segoe UI", 16f)
        };

        var confirm = new Button
        {
            Text         = "Confirm",
            Location     = new Point(110, 348),
            Size         = new Size(140, 34),
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(59, 130, 246),
            ForeColor    = Color.White
        };

        var cancel = new Button
        {
            Text         = "Cancel",
            Location     = new Point(110, 388),
            Size         = new Size(140, 28),
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.Flat,
            ForeColor    = Color.White
        };

        _error = new Label
        {
            Location  = new Point(20, 424),
            Size      = new Size(320, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(247, 168, 178)
        };

        Controls.AddRange([intro, qr, secretLabel, _code, confirm, cancel, _error]);
        AcceptButton = confirm;
        CancelButton = cancel;
    }

    /// <summary>Shows an error and keeps the dialog open for a retry.</summary>
    public void SetError(string message)
    {
        _error.Text = message;
        _code.SelectAll();
        _code.Focus();
    }

    private static Image RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png  = new PngByteQRCode(data).GetGraphic(6);
        using var ms = new MemoryStream(png);
        return Image.FromStream(ms);
    }

    private static string? ExtractSecret(string uri)
    {
        var i = uri.IndexOf("secret=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = uri[(i + 7)..];
        var amp  = rest.IndexOf('&');
        return amp < 0 ? rest : rest[..amp];
    }
}
