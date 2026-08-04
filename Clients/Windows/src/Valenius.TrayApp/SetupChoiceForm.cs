using System.Drawing;

namespace Valenius.TrayApp;

/// <summary>
/// First-run modal dialog shown when the installer provided no backend URL, replacing what used
/// to jump straight to <see cref="BackendUrlForm"/>. Lets the user pick between connecting to a
/// Valenius server now, or using the client entirely standalone (local profiles only, no backend
/// ever contacted) with the option to connect to a server later from the About dialog.
///
/// Laid out by hand from the real monitor DPI, same rationale and pattern as
/// <see cref="BackendUrlForm"/>'s <c>ApplyScale()</c>/<c>Sc()</c> — see that class's remarks.
/// </summary>
internal sealed class SetupChoiceForm : Form
{
    private const int BaseW    = 400;
    private const int BasePad  = 20;
    private const int BaseGap  = 12;
    private const int BaseBtnH = 40;

    private readonly Label  _intro;
    private readonly Button _connect;
    private readonly Button _standalone;

    public SetupChoiceForm()
    {
        Text            = "Set up Valenius";
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
            Text      = "Does your organization have a Valenius server? Connect to it now, or use " +
                        "Valenius standalone with your own WireGuard configs — you can connect to a " +
                        "server later from the About dialog.",
            AutoSize  = true,
            TextAlign = ContentAlignment.TopLeft
        };

        _connect = new Button
        {
            Text         = "Connect to a server",
            DialogResult = DialogResult.Yes,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(59, 130, 246),
            ForeColor    = Color.White
        };
        _connect.FlatAppearance.BorderSize = 0;

        _standalone = new Button
        {
            Text         = "Use standalone",
            DialogResult = DialogResult.No,
            FlatStyle    = FlatStyle.Flat,
            ForeColor    = Color.White
        };

        Controls.AddRange([_intro, _connect, _standalone]);
        AcceptButton = _connect;
        CancelButton = null;
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

    private void ApplyScale()
    {
        float scale = DeviceDpi / 96f;
        int Sc(int v) => (int)MathF.Round(v * scale);

        var baseFont = new Font("Segoe UI", 9f);
        Font = baseFont;

        int pad   = Sc(BasePad);
        int gap   = Sc(BaseGap);
        int width = Sc(BaseW) - pad * 2;

        _intro.Font        = baseFont;
        _intro.MaximumSize = new Size(width, 0);
        _intro.Location    = new Point(pad, pad);

        int y = _intro.Bottom + gap * 2;

        int btnH = Sc(BaseBtnH);
        _connect.Font     = baseFont;
        _connect.Location = new Point(pad, y);
        _connect.Size     = new Size(width, btnH);

        y = _connect.Bottom + gap;

        _standalone.Font     = baseFont;
        _standalone.Location = new Point(pad, y);
        _standalone.Size     = new Size(width, btnH);

        ClientSize = new Size(Sc(BaseW), _standalone.Bottom + pad);
    }
}
