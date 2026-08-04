using System.Drawing;

namespace Valenius.TrayApp;

/// <summary>
/// First-run modal dialog shown when the installer provided no backend URL. The user types only the
/// server DNS host; the "https://" scheme is fixed (shown as a non-editable prefix) and cannot be
/// changed. The caller sends the entered host to the service (SetBackendUrl) and reports failures via
/// <see cref="SetError"/>.
///
/// Laid out manually from the real monitor DPI (like <see cref="TrayPopupForm"/>'s <c>Sc()</c>),
/// not <c>AutoScaleMode.Dpi</c>: that path was observed (real bug report, high-DPI monitor) scaling
/// control Fonts but not their fixed pixel Location/Size, which clipped the wrapped intro/hint text
/// and the "https://" prefix, and left a large blank area below the buttons because the Form's own
/// ClientSize scaled independently of its (unscaled) children. <see cref="ApplyScale"/> recomputes
/// every Font and Bounds from <see cref="DeviceDpi"/> and sizes the Form to the actual laid-out
/// content, so it can't clip regardless of DPI or how long the text ends up being.
/// </summary>
internal sealed class BackendUrlForm : Form
{
    private const int BaseW      = 400;
    private const int BasePad    = 20;
    private const int BaseGap    = 10;
    private const int BaseFieldH = 30;
    private const int BaseBtnH   = 34;

    private readonly Label   _intro;
    private readonly Label   _scheme;
    private readonly TextBox _dns;
    private readonly Label   _hint;
    private readonly Button  _save;
    private readonly Button  _cancel;
    private readonly Label   _error;

    /// <summary>The DNS host the user entered (scheme/paths are normalized away by the service).</summary>
    public string EnteredDns => _dns.Text.Trim();

    public BackendUrlForm(string? currentDns = null)
    {
        Text            = "Connect to your Valenius server";
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
            Text      = "Enter the address of your Valenius server. Your administrator provided this — " +
                        "just the server name, for example vpn.company.com.",
            AutoSize  = true,
            TextAlign = ContentAlignment.TopLeft
        };

        // Fixed, non-editable scheme prefix rendered to look like the left edge of the field.
        var scheme = new Label
        {
            Text      = "https://",
            AutoSize  = true,
            ForeColor = Color.FromArgb(180, 198, 222)
        };
        _scheme = scheme;

        _dns = new TextBox
        {
            Text        = currentDns ?? "",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor   = Color.FromArgb(15, 23, 36),
            ForeColor   = Color.White
        };

        _hint = new Label
        {
            Text      = "Do not include https:// or any path — just the server name.",
            AutoSize  = true,
            ForeColor = Color.FromArgb(150, 168, 192)
        };

        _save = new Button
        {
            Text         = "Save",
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(59, 130, 246),
            ForeColor    = Color.White
        };
        _save.FlatAppearance.BorderSize = 0;

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
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(247, 168, 178)
        };

        Controls.AddRange([_intro, _scheme, _dns, _hint, _save, _cancel, _error]);
        AcceptButton = _save;
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

        var baseFont  = new Font("Segoe UI", 9f);
        var fieldFont = new Font("Segoe UI", 11f);
        var hintFont  = new Font("Segoe UI", 8f);

        Font = baseFont;

        int pad   = Sc(BasePad);
        int gap   = Sc(BaseGap);
        int width = Sc(BaseW) - pad * 2;

        _intro.Font        = baseFont;
        _intro.MaximumSize = new Size(width, 0);
        _intro.Location    = new Point(pad, pad);

        int y = _intro.Bottom + gap;

        int fieldH = Sc(BaseFieldH);
        _scheme.Font = fieldFont;
        int schemeY = y + Math.Max(0, (fieldH - _scheme.PreferredSize.Height) / 2);
        _scheme.Location = new Point(pad, schemeY);

        int dnsX = pad + _scheme.PreferredSize.Width + Sc(4);
        _dns.Font     = fieldFont;
        _dns.Location = new Point(dnsX, y);
        _dns.Size     = new Size(pad + width - dnsX, fieldH);

        y += fieldH + Sc(4);

        _hint.Font        = hintFont;
        _hint.MaximumSize = new Size(width, 0);
        _hint.Location    = new Point(pad, y);

        y = _hint.Bottom + gap * 2;

        int btnH   = Sc(BaseBtnH);
        int btnGap = gap;
        int btnW   = (width - btnGap) / 2;

        _cancel.Font     = baseFont;
        _cancel.Location = new Point(pad, y);
        _cancel.Size     = new Size(btnW, btnH);

        _save.Font     = baseFont;
        _save.Location = new Point(pad + btnW + btnGap, y);
        _save.Size     = new Size(btnW, btnH);

        y = _save.Bottom + gap;

        _error.Font        = baseFont;
        _error.MaximumSize = new Size(width, 0);
        _error.Location    = new Point(pad, y);

        ClientSize = new Size(Sc(BaseW), _error.Bottom + pad);
    }

    /// <summary>Shows an error and keeps the dialog open for a retry.</summary>
    public void SetError(string message)
    {
        _error.Text = message;
        ApplyScale(); // the error text may now need more room than the empty-state reservation
        _dns.SelectAll();
        _dns.Focus();
    }
}
