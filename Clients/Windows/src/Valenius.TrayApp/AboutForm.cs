using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Valenius.TrayApp;

/// <summary>
/// Laid out manually from the real monitor DPI (like <see cref="TrayPopupForm"/>,
/// <see cref="BackendUrlForm"/>, <see cref="MfaEnrollForm"/>), not <c>AutoScaleMode.Dpi</c>,
/// for consistency with those. This form's content is already self-sizing (AutoSize Labels/
/// Buttons in an AutoSize TableLayoutPanel/FlowLayoutPanel, Font specified in points which is
/// DPI-correct on its own) so it was never at risk of the clipping bug the other dialogs had --
/// the only genuinely fixed-pixel values are the logo's Size and the Margin/Padding spacing,
/// which <see cref="ApplyScale"/> now scales explicitly instead of relying on PerformAutoScale.
/// </summary>
internal sealed class AboutForm : Form
{
    /// <summary>Invoked when the user clicks "Send diagnostic logs". Wired by the tray to send the SendLogs IPC command.</summary>
    internal Action? SendLogsRequested;

    /// <summary>Invoked when a standalone user clicks "Connect to a server". Wired by the tray to
    /// reopen the same backend-URL prompt used at first run.</summary>
    internal Action? ConnectToServerRequested;

    private const int BaseLogoW = 260;
    private const int BaseLogoH = 60;
    private const int BasePad   = 24;

    private readonly PictureBox         _logo;
    private readonly Label              _appNameLabel;
    private readonly Label              _versionLabel;
    private readonly Label              _devLabel;
    private readonly LinkLabel          _webLink;
    private readonly Label              _supportLabel;
    private readonly LinkLabel          _emailLink;
    private readonly Label              _statusDot;
    private readonly Label              _statusText;
    private readonly FlowLayoutPanel    _statusRow;
    private readonly Button             _okButton;
    private readonly Button             _sendLogsButton;
    private readonly Button             _connectButton;
    private readonly FlowLayoutPanel    _buttonPanel;
    private readonly TableLayoutPanel   _table;
    private readonly bool               _standaloneMode;

    internal AboutForm(bool standaloneMode = false)
    {
        _standaloneMode = standaloneMode;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";

        // ── Logo ──────────────────────────────────────────────────────────────
        _logo = new PictureBox
        {
            Image    = LoadLogoImage(),
            SizeMode = PictureBoxSizeMode.Zoom,
        };

        // ── App name + version ────────────────────────────────────────────────
        _appNameLabel = new Label
        {
            Text     = "Valenius",
            Font     = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
        };

        _versionLabel = new Label
        {
            Text     = versionText,
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
        };

        // ── "Developed by" ────────────────────────────────────────────────────
        _devLabel = new Label
        {
            Text     = "Developed by Stranto Business Solutions GmbH",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
        };

        _webLink = new LinkLabel
        {
            Text     = "www.stranto.com",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
        };
        _webLink.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo("https://www.stranto.com/") { UseShellExecute = true });

        // ── Support ───────────────────────────────────────────────────────────
        _supportLabel = new Label
        {
            Text     = "If you need support, please contact:",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
        };

        _emailLink = new LinkLabel
        {
            Text     = "helpdesk@stranto.com",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
        };
        _emailLink.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo("mailto:helpdesk@stranto.com") { UseShellExecute = true });

        // ── Backend status ────────────────────────────────────────────────────
        _statusDot = new Label
        {
            Text      = "●",
            Font      = new Font("Segoe UI", 11f),
            AutoSize  = true,
            ForeColor = Color.Silver,
        };

        _statusText = new Label
        {
            Text      = "Checking backend…",
            Font      = new Font("Segoe UI", 9f),
            AutoSize  = true,
            ForeColor = Color.Gray,
        };

        _statusRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Anchor        = AnchorStyles.None,
        };
        _statusRow.Controls.Add(_statusDot);
        _statusRow.Controls.Add(_statusText);

        // ── OK button ─────────────────────────────────────────────────────────
        _okButton = new Button
        {
            Text     = "OK",
            AutoSize = true,
            Anchor   = AnchorStyles.None,   // centred by FlowLayoutPanel below
        };
        _okButton.Click += (_, _) => Close();

        // ── Send diagnostic logs ──────────────────────────────────────────────
        _sendLogsButton = new Button
        {
            Text     = "Send diagnostic logs",
            AutoSize = true,
            Anchor   = AnchorStyles.None,
        };
        new ToolTip().SetToolTip(_sendLogsButton,
            "Uploads Valenius-only logs (service log, update log, WireGuard log) to your administrator for support. "
            + "Secrets are redacted before sending; no other system data is collected.");
        _sendLogsButton.Click += (_, _) =>
        {
            SendLogsRequested?.Invoke();
            _sendLogsButton.Enabled = false;
            _sendLogsButton.Text    = "Logs sent — thank you";
        };
        _sendLogsButton.Visible = !_standaloneMode; // nothing to send logs to without a backend

        // ── Connect to a server (standalone only) ─────────────────────────────
        _connectButton = new Button
        {
            Text     = "Connect to a server",
            AutoSize = true,
            Anchor   = AnchorStyles.None,
            Visible  = _standaloneMode,
        };
        _connectButton.Click += (_, _) =>
        {
            ConnectToServerRequested?.Invoke();
            Close();
        };

        _buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Anchor        = AnchorStyles.None,
        };
        _buttonPanel.Controls.Add(_sendLogsButton);
        _buttonPanel.Controls.Add(_connectButton);
        _buttonPanel.Controls.Add(_okButton);

        // ── Layout ────────────────────────────────────────────────────────────
        _table = new TableLayoutPanel
        {
            ColumnCount  = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Control[] rows = [_logo, _appNameLabel, _versionLabel,
                          _devLabel, _webLink, _supportLabel, _emailLink, _statusRow, _buttonPanel];
        foreach (var (ctrl, i) in rows.Select((c, i) => (c, i)))
        {
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _table.Controls.Add(ctrl, 0, i);
            ctrl.Anchor = AnchorStyles.None;   // horizontally centred in each row
        }

        // ── Backend reachability check ────────────────────────────────────────
        if (_standaloneMode)
        {
            // No backend configured at all — a reachability probe would be meaningless (and
            // ReadBackendUrl() would fall back to a placeholder default). Say so plainly instead.
            _statusDot.ForeColor  = Color.Silver;
            _statusText.ForeColor = Color.Gray;
            _statusText.Text      = "Standalone — no server configured";
        }
        else
        {
            Load += async (_, _) =>
            {
                var url       = ReadBackendUrl();
                var reachable = await CheckBackendAsync(url);
                BeginInvoke(() =>
                {
                    _statusDot.ForeColor  = reachable ? Color.FromArgb(0, 160, 0) : Color.Red;
                    _statusText.ForeColor = reachable ? Color.FromArgb(0, 120, 0) : Color.Red;
                    _statusText.Text      = reachable ? "Backend reachable" : "Backend unreachable";
                });
            };
        }

        // ── Form settings ─────────────────────────────────────────────────────
        Text            = "About Valenius";
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        AutoScaleMode   = AutoScaleMode.None; // spacing scaled by hand in ApplyScale() -- see class remarks
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        AcceptButton    = _okButton;
        Controls.Add(_table);
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
    /// Scales the logo image and every Margin/Padding spacing value from the monitor's real DPI.
    /// Fonts are left untouched -- they're specified in points, which render at the correct
    /// pixel size for the current DPI on their own, without needing PerformAutoScale.
    /// </summary>
    private void ApplyScale()
    {
        float scale = DeviceDpi / 96f;
        int Sc(int v) => (int)MathF.Round(v * scale);

        _logo.Size = new Size(Sc(BaseLogoW), Sc(BaseLogoH));
        _logo.Margin = new Padding(0, 0, 0, Sc(16));

        _appNameLabel.Margin = new Padding(0, 0, 0, Sc(2));
        _versionLabel.Margin = new Padding(0, 0, 0, Sc(18));
        _devLabel.Margin     = new Padding(0, 0, 0, Sc(2));
        _webLink.Margin      = new Padding(0, 0, 0, Sc(20));
        _supportLabel.Margin = new Padding(0, 0, 0, Sc(2));
        _emailLink.Margin    = new Padding(0, 0, 0, Sc(20));

        _statusDot.Margin  = new Padding(0, Sc(1), Sc(4), 0);
        _statusText.Margin = new Padding(0, Sc(2), 0, 0);
        _statusRow.Margin  = new Padding(0, 0, 0, Sc(14));

        _okButton.MinimumSize       = new Size(Sc(80), 0);
        _sendLogsButton.MinimumSize = new Size(Sc(80), 0);
        _sendLogsButton.Margin      = new Padding(0, 0, Sc(8), 0);
        _connectButton.MinimumSize  = new Size(Sc(80), 0);
        _connectButton.Margin       = new Padding(0, 0, Sc(8), 0);
        _buttonPanel.Margin         = new Padding(0);

        _table.Padding = new Padding(Sc(BasePad));

        PerformLayout();
    }

    private static string ReadBackendUrl()
    {
        // Mirrors BackendUrlProvider's own precedence: a persisted user/MDM-set URL in
        // backend.dat always wins over the install-time appsettings.json default.
        var backendDatPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Valenius", "backend.dat");

        if (File.Exists(backendDatPath))
        {
            try
            {
                var persisted = File.ReadAllText(backendDatPath).Trim();
                if (!string.IsNullOrEmpty(persisted))
                    return persisted.TrimEnd('/');
            }
            catch { }
        }

        // Production layout: {app}\trayapp\  →  {app}\service\appsettings.json
        var candidate = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "service", "appsettings.json"));

        if (!File.Exists(candidate))
            candidate = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (File.Exists(candidate))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                if (doc.RootElement.TryGetProperty("Valenius", out var wt) &&
                    wt.TryGetProperty("BackendUrl", out var urlProp))
                    return urlProp.GetString()?.TrimEnd('/') ?? "https://valenius.stranto.com";
            }
            catch { }
        }

        return "https://valenius.stranto.com";
    }

    private static async Task<bool> CheckBackendAsync(string backendUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync(backendUrl + "/api/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static Image LoadLogoImage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string name = "Valenius.TrayApp.Resources.stranto-logo-full.png";
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return new Bitmap(1, 1);
        return Image.FromStream(new MemoryStream(ReadAllBytes(stream)));
    }

    private static byte[] ReadAllBytes(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
