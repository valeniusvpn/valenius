using System.Drawing;

namespace Valenius.TrayApp;

/// <summary>
/// First-run modal dialog shown when the installer provided no backend URL. The user types only the
/// server DNS host; the "https://" scheme is fixed (shown as a non-editable prefix) and cannot be
/// changed. The caller sends the entered host to the service (SetBackendUrl) and reports failures via
/// <see cref="SetError"/>. Styling mirrors <see cref="MfaEnrollForm"/>.
/// </summary>
internal sealed class BackendUrlForm : Form
{
    private readonly TextBox _dns;
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
        ClientSize      = new Size(400, 232);
        BackColor       = Color.FromArgb(22, 32, 48);
        ForeColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode       = AutoScaleMode.Dpi;

        var intro = new Label
        {
            Text      = "Enter the address of your Valenius server. Your administrator provided this — " +
                        "just the server name, for example vpn.company.com.",
            Location  = new Point(20, 16),
            Size      = new Size(360, 52),
            TextAlign = ContentAlignment.TopLeft
        };

        // Fixed, non-editable scheme prefix rendered to look like the left edge of the field.
        var scheme = new Label
        {
            Text        = "https://",
            Location    = new Point(20, 82),
            Size        = new Size(58, 30),
            TextAlign   = ContentAlignment.MiddleLeft,
            ForeColor   = Color.FromArgb(180, 198, 222),
            Font        = new Font("Segoe UI", 11f)
        };

        _dns = new TextBox
        {
            Location   = new Point(78, 82),
            Size       = new Size(302, 30),
            Text       = currentDns ?? "",
            Font       = new Font("Segoe UI", 11f),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor  = Color.FromArgb(15, 23, 36),
            ForeColor  = Color.White
        };

        var hint = new Label
        {
            Text      = "Do not include https:// or any path — just the server name.",
            Location  = new Point(20, 118),
            Size      = new Size(360, 18),
            ForeColor = Color.FromArgb(150, 168, 192),
            Font      = new Font("Segoe UI", 8f)
        };

        var save = new Button
        {
            Text         = "Save",
            Location     = new Point(206, 150),
            Size         = new Size(174, 34),
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(59, 130, 246),
            ForeColor    = Color.White
        };
        save.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text         = "Cancel",
            Location     = new Point(20, 150),
            Size         = new Size(174, 34),
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.Flat,
            ForeColor    = Color.White
        };

        _error = new Label
        {
            Location  = new Point(20, 192),
            Size      = new Size(360, 32),
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(247, 168, 178)
        };

        Controls.AddRange([intro, scheme, _dns, hint, save, cancel, _error]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    /// <summary>Shows an error and keeps the dialog open for a retry.</summary>
    public void SetError(string message)
    {
        _error.Text = message;
        _dns.SelectAll();
        _dns.Focus();
    }
}
