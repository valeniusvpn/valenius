using System.Drawing;
using QRCoder;

namespace Valenius.TrayApp;

/// <summary>
/// Modal dialog for per-client TOTP enrollment: renders the otpauth QR, shows the secret
/// for manual entry, and collects the confirmation code. The caller sends the code to the
/// service (MfaEnrollConfirm) and shows the result via <see cref="SetError"/>.
///
/// Laid out manually from the real monitor DPI (like <see cref="TrayPopupForm"/>'s <c>Sc()</c>
/// and <see cref="BackendUrlForm"/>), not <c>AutoScaleMode.Dpi</c>: that path scales control
/// Fonts but not their fixed pixel Location/Size, which clips wrapped text and leaves the
/// Form's own (correctly-scaled) client area mismatched with its (unscaled) children on a
/// high-DPI monitor. <see cref="ApplyScale"/> recomputes every Font and Bounds from
/// <see cref="DeviceDpi"/> and sizes the Form to the actual laid-out content.
/// </summary>
internal sealed class MfaEnrollForm : Form
{
    private const int BaseW         = 360;
    private const int BasePadTop    = 12;
    private const int BasePadSide   = 20;
    private const int BasePadBottom = 12;
    private const int BaseGap       = 8;
    private const int BaseQrSize    = 200;
    private const int BaseCodeW     = 140;
    private const int BaseCodeH     = 34;
    private const int BaseBtnW      = 140;
    private const int BaseConfirmH  = 34;
    private const int BaseCancelH   = 28;

    private readonly Label     _intro;
    private readonly PictureBox _qr;
    private readonly Label     _secretLabel;
    private readonly TextBox   _code;
    private readonly Button    _confirm;
    private readonly Button    _cancel;
    private readonly Label     _error;

    public string EnteredCode => _code.Text.Trim();

    public MfaEnrollForm(string otpAuthUri)
    {
        Text            = "Set up two-factor authentication";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        BackColor       = Color.FromArgb(22, 32, 48);
        ForeColor       = Color.White;
        AutoScaleMode   = AutoScaleMode.None; // scaled by hand in ApplyScale() -- see class remarks

        _intro = new Label
        {
            Text      = "Scan this with your authenticator app (Google Authenticator, Microsoft Authenticator, etc.), then enter the 6-digit code to finish setup.",
            AutoSize  = true,
            TextAlign = ContentAlignment.TopCenter
        };

        _qr = new PictureBox
        {
            SizeMode  = PictureBoxSizeMode.StretchImage,
            BackColor = Color.White,
            Image     = RenderQr(otpAuthUri)
        };

        var secret = ExtractSecret(otpAuthUri);
        _secretLabel = new Label
        {
            Text      = secret is null ? "" : $"Manual key: {secret}",
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(180, 198, 222)
        };

        _code = new TextBox
        {
            MaxLength = 6,
            TextAlign = HorizontalAlignment.Center
        };

        _confirm = new Button
        {
            Text         = "Confirm",
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(59, 130, 246),
            ForeColor    = Color.White
        };

        _cancel = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.Flat,
            ForeColor    = Color.White
        };

        _error = new Label
        {
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(247, 168, 178)
        };

        Controls.AddRange([_intro, _qr, _secretLabel, _code, _confirm, _cancel, _error]);
        AcceptButton = _confirm;
        CancelButton = _cancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyScale();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale();
    }

    /// <summary>
    /// Recomputes every control's Font and Bounds from the monitor's real DPI, then shrink-wraps
    /// the Form's ClientSize to the resulting content. Safe to call repeatedly (DPI change, or
    /// after <see cref="SetError"/> changes how tall the error label needs to be).
    /// </summary>
    private void ApplyScale()
    {
        float scale = DeviceDpi / 96f;
        int Sc(int v) => (int)MathF.Round(v * scale);

        var baseFont   = new Font("Segoe UI", 9f);
        var secretFont = new Font("Consolas", 8f);
        var codeFont   = new Font("Segoe UI", 16f);

        Font = baseFont;

        int pad   = Sc(BasePadSide);
        int gap   = Sc(BaseGap);
        int width = Sc(BaseW) - pad * 2;

        _intro.Font        = baseFont;
        _intro.MinimumSize = new Size(width, 0);
        _intro.MaximumSize = new Size(width, 0);
        _intro.Location    = new Point(pad, Sc(BasePadTop));

        int y = _intro.Bottom + gap;

        int qrSize   = Sc(BaseQrSize);
        _qr.Size     = new Size(qrSize, qrSize);
        _qr.Location = new Point(pad + (width - qrSize) / 2, y);

        y = _qr.Bottom + gap;

        _secretLabel.Font     = secretFont;
        _secretLabel.Location = new Point(pad + Math.Max(0, (width - _secretLabel.PreferredSize.Width) / 2), y);

        y = _secretLabel.Bottom + gap;

        int codeW = Sc(BaseCodeW), codeH = Sc(BaseCodeH);
        _code.Font     = codeFont;
        _code.Size     = new Size(codeW, codeH);
        _code.Location = new Point(pad + (width - codeW) / 2, y);

        y = _code.Bottom + gap;

        int btnW     = Sc(BaseBtnW);
        int confirmH = Sc(BaseConfirmH);
        _confirm.Font     = baseFont;
        _confirm.Size     = new Size(btnW, confirmH);
        _confirm.Location = new Point(pad + (width - btnW) / 2, y);

        y = _confirm.Bottom + gap;

        int cancelH = Sc(BaseCancelH);
        _cancel.Font     = baseFont;
        _cancel.Size     = new Size(btnW, cancelH);
        _cancel.Location = new Point(pad + (width - btnW) / 2, y);

        y = _cancel.Bottom + gap;

        _error.Font        = baseFont;
        _error.MinimumSize = new Size(width, 0);
        _error.MaximumSize = new Size(width, 0);
        _error.Location    = new Point(pad, y);

        ClientSize = new Size(Sc(BaseW), _error.Bottom + Sc(BasePadBottom));
    }

    /// <summary>Shows an error and keeps the dialog open for a retry.</summary>
    public void SetError(string message)
    {
        _error.Text = message;
        ApplyScale(); // the error text may now need more room than the empty-state reservation
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
