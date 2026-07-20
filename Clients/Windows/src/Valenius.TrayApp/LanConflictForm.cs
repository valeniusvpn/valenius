using System.Drawing;

namespace Valenius.TrayApp;

/// <summary>
/// Blocking modal shown when the service refuses to connect because the machine's own local
/// LAN conflicts with a network reachable through the VPN (or the VPN IP it would be assigned).
/// This is a hard refusal — the connect attempt already failed server-side; there is no "connect
/// anyway" here, only acknowledgement. A modal is used (rather than the usual balloon/toast used
/// for other connect failures) because this warning must not be missed or auto-dismissed.
/// Styling mirrors <see cref="BackendUrlForm"/>/<see cref="MfaEnrollForm"/>, but every control's
/// colors are set explicitly (not left to ambient inheritance) and the body wraps/auto-sizes to
/// the message instead of a fixed box, since the conflict detail can run to a few sentences.
///
/// The final ClientSize/button position are computed in <see cref="OnLoad"/>, not the
/// constructor: AutoScaleMode.Dpi scales every child control's Bounds/MaximumSize/Font once the
/// handle is created (before Load), so <c>body</c>'s AutoSize height is only correct — reflecting
/// the real DPI and the resulting wrapped line count — after that pass has run. Computing sizes
/// in the constructor instead reads pre-scale numbers and lets the framework's own scale pass
/// apply on top, double-scaling and clipping content on any non-96-DPI display.
/// </summary>
internal sealed class LanConflictForm : Form
{
    private static readonly Color Background = Color.FromArgb(22, 32, 48);
    private static readonly Color BodyText    = Color.FromArgb(226, 232, 240);

    private const int EdgeMargin      = 20;
    private const int IconSize    = 40;
    private const int ContentWidth = 380;

    private readonly Label _body;
    private readonly Button _ok;

    public LanConflictForm(string message)
    {
        Text            = "Network conflict — can't connect";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        BackColor       = Background;
        ForeColor       = BodyText;
        Font            = new Font("Segoe UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode       = AutoScaleMode.Dpi;

        var icon = new PictureBox
        {
            Image     = SystemIcons.Warning.ToBitmap(),
            SizeMode  = PictureBoxSizeMode.StretchImage,
            Location  = new Point(EdgeMargin, EdgeMargin),
            Size      = new Size(IconSize, IconSize),
            BackColor = Background
        };

        var heading = new Label
        {
            Text        = "This VPN was not connected",
            Location    = new Point(EdgeMargin + IconSize + 14, EdgeMargin + 8),
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth - IconSize - 14, 0),
            Font        = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor   = Color.White,
            BackColor   = Background
        };

        _body = new Label
        {
            Text        = message,
            Location    = new Point(EdgeMargin, EdgeMargin + IconSize + 16),
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor   = BodyText,
            BackColor   = Background
        };

        _ok = new Button
        {
            Text         = "OK",
            Size         = new Size(100, 32),
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(59, 130, 246),
            ForeColor    = Color.White
        };
        _ok.FlatAppearance.BorderSize = 0;

        Controls.AddRange([icon, heading, _body, _ok]);
        AcceptButton = _ok;
        CancelButton = _ok;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Runs after AutoScaleMode.Dpi has scaled every control (Bounds, MaximumSize, Font), so
        // _body.Height here reflects the real wrapped line count at the real DPI — see class doc.
        var gap = (int)Math.Round(20 * DeviceDpi / 96f);
        var margin = (int)Math.Round(EdgeMargin * DeviceDpi / 96f);
        _ok.Location = new Point(_body.Right - _ok.Width, _body.Bottom + gap);
        ClientSize    = new Size(_body.Right + margin, _ok.Bottom + margin);
    }
}
