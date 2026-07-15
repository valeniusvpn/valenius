using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Valenius.TrayApp;

internal sealed class AboutForm : Form
{
    /// <summary>Invoked when the user clicks "Send diagnostic logs". Wired by the tray to send the SendLogs IPC command.</summary>
    internal Action? SendLogsRequested;

    internal AboutForm()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";

        // ── Logo ──────────────────────────────────────────────────────────────
        var logoPictureBox = new PictureBox
        {
            Image       = LoadLogoImage(),
            SizeMode    = PictureBoxSizeMode.Zoom,
            Size        = new Size(260, 60),
            Margin      = new Padding(0, 0, 0, 16),
        };

        // ── App name + version ────────────────────────────────────────────────
        var appNameLabel = new Label
        {
            Text      = "Valenius",
            Font      = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize  = true,
            Margin    = new Padding(0, 0, 0, 2),
        };

        var versionLabel = new Label
        {
            Text     = versionText,
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 18),
        };

        // ── "Developed by" ────────────────────────────────────────────────────
        var devLabel = new Label
        {
            Text     = "Developed by Stranto Business Solutions GmbH",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 2),
        };

        var webLink = new LinkLabel
        {
            Text     = "www.stranto.com",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 20),
        };
        webLink.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo("https://www.stranto.com/") { UseShellExecute = true });

        // ── Support ───────────────────────────────────────────────────────────
        var supportLabel = new Label
        {
            Text     = "If you need support, please contact:",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 2),
        };

        var emailLink = new LinkLabel
        {
            Text     = "helpdesk@stranto.com",
            Font     = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin   = new Padding(0, 0, 0, 20),
        };
        emailLink.LinkClicked += (_, _) =>
            Process.Start(new ProcessStartInfo("mailto:helpdesk@stranto.com") { UseShellExecute = true });

        // ── Backend status ────────────────────────────────────────────────────
        var statusDot = new Label
        {
            Text      = "●",
            Font      = new Font("Segoe UI", 11f),
            AutoSize  = true,
            ForeColor = Color.Silver,
            Margin    = new Padding(0, 1, 4, 0),
        };

        var statusText = new Label
        {
            Text      = "Checking backend…",
            Font      = new Font("Segoe UI", 9f),
            AutoSize  = true,
            ForeColor = Color.Gray,
            Margin    = new Padding(0, 2, 0, 0),
        };

        var statusRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Anchor        = AnchorStyles.None,
            Margin        = new Padding(0, 0, 0, 14),
        };
        statusRow.Controls.Add(statusDot);
        statusRow.Controls.Add(statusText);

        // ── OK button ─────────────────────────────────────────────────────────
        var okButton = new Button
        {
            Text        = "OK",
            AutoSize    = true,
            MinimumSize = new Size(80, 0),
            Anchor      = AnchorStyles.None,   // centred by FlowLayoutPanel below
        };
        okButton.Click += (_, _) => Close();

        // ── Send diagnostic logs ──────────────────────────────────────────────
        var sendLogsButton = new Button
        {
            Text        = "Send diagnostic logs",
            AutoSize    = true,
            MinimumSize = new Size(80, 0),
            Anchor      = AnchorStyles.None,
            Margin      = new Padding(0, 0, 8, 0),
        };
        new ToolTip().SetToolTip(sendLogsButton,
            "Uploads Valenius-only logs (service log, update log, WireGuard log) to your administrator for support. "
            + "Secrets are redacted before sending; no other system data is collected.");
        sendLogsButton.Click += (_, _) =>
        {
            SendLogsRequested?.Invoke();
            sendLogsButton.Enabled = false;
            sendLogsButton.Text    = "Logs sent — thank you";
        };

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Anchor        = AnchorStyles.None,
            Margin        = new Padding(0),
        };
        buttonPanel.Controls.Add(sendLogsButton);
        buttonPanel.Controls.Add(okButton);

        // ── Layout ────────────────────────────────────────────────────────────
        var table = new TableLayoutPanel
        {
            ColumnCount  = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(24),
            Margin       = new Padding(0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Control[] rows = [logoPictureBox, appNameLabel, versionLabel,
                          devLabel, webLink, supportLabel, emailLink, statusRow, buttonPanel];
        foreach (var (ctrl, i) in rows.Select((c, i) => (c, i)))
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(ctrl, 0, i);
            ctrl.Anchor = AnchorStyles.None;   // horizontally centred in each row
        }

        // ── Backend reachability check ────────────────────────────────────────
        Load += async (_, _) =>
        {
            var url       = ReadBackendUrl();
            var reachable = await CheckBackendAsync(url);
            BeginInvoke(() =>
            {
                statusDot.ForeColor  = reachable ? Color.FromArgb(0, 160, 0) : Color.Red;
                statusText.ForeColor = reachable ? Color.FromArgb(0, 120, 0) : Color.Red;
                statusText.Text      = reachable ? "Backend reachable" : "Backend unreachable";
            });
        };

        // ── Form settings ─────────────────────────────────────────────────────
        Text                = "About Valenius";
        StartPosition       = FormStartPosition.CenterScreen;
        FormBorderStyle     = FormBorderStyle.FixedDialog;
        MaximizeBox         = false;
        MinimizeBox         = false;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoSize            = true;
        AutoSizeMode        = AutoSizeMode.GrowAndShrink;
        AcceptButton        = okButton;
        Controls.Add(table);
    }

    private static string ReadBackendUrl()
    {
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
