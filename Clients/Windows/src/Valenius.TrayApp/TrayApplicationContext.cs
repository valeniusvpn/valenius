using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valenius.Shared;

namespace Valenius.TrayApp;

public class TrayApplicationContext : ApplicationContext
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon     _trayIcon;
    private readonly TrayPopupForm  _popup;
    private readonly Form           _anchor;

    private static readonly Image _appImage = LoadEmbeddedImage("app.png");
    private readonly Icon _iconConnected;
    private readonly Icon _iconDisconnected;
    private readonly Icon _iconUnavailable;

    private readonly PipeClient _pipe = new();
    private readonly System.Windows.Forms.Timer _pollTimer;

    private TunnelStatus _lastStatus   = new();
    private bool         _serviceUp    = false;
    private bool         _claimingConfig;
    private AboutForm?   _aboutForm;
    private bool         _mfaEnrollOpen;
    private bool         _uploadConfigOpen;
    private bool         _backendUrlOpen;
    private bool         _backendPromptAutoShown;
    private bool         _setupChoiceOpen;

    public TrayApplicationContext()
    {
        _iconConnected    = LoadEmbeddedIcon("app-connected.ico");
        _iconDisconnected = LoadEmbeddedIcon("app-disconnected.ico");
        _iconUnavailable  = BuildUnavailableIcon();

        _anchor = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar   = false,
            Size            = new Size(1, 1),
            Location        = new Point(-32000, -32000),
            Text            = string.Empty
        };
        _anchor.Show();
        _anchor.Hide();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _pollTimer.Tick += (_, _) => _ = RefreshStatusAsync();

        _popup = new TrayPopupForm(_appImage);
        _popup.ConnectRequested       += async p => await ConnectWithProfileAsync(p);
        _popup.DisconnectRequested    += async p => await OnDisconnect(p);
        _popup.DisconnectAllRequested += async () => await OnDisconnectAll();
        _popup.UploadConfigRequested  += OnUploadConfig;
        _popup.AutoConnectToggled     += async () => await OnAutoConnectToggle();
        _popup.InstallUpdateRequested += async () => await OnInstallUpdate();
        _popup.RegisterRequested      += async () => await OnRegisterClient();
        _popup.DeleteProfileRequested += async p => await OnDeleteProfileAsync(p);
        _popup.AboutRequested         += OnAboutRequested;
        _popup.MfaAuthorizeRequested  += OnMfaAuthorize;
        _popup.MfaEnrollRequested     += async uri => await OnMfaEnrollAsync(uri);
        _popup.ExitRequested          += OnExit;

        // When popup closes restart the poll timer and refresh status
        _popup.VisibleChanged += (_, _) =>
        {
            if (!_popup.Visible)
            {
                _pollTimer.Start();
                _ = RefreshStatusAsync();
            }
        };

        _trayIcon = new NotifyIcon
        {
            Icon    = _iconUnavailable,
            Visible = true,
            Text    = "Valenius"
        };
        _trayIcon.MouseClick += OnTrayMouseClick;

        _pollTimer.Start();

        // Live backend round-trip (not the cheap cached Status) on every tray start, so a
        // freshly-launched tray -- even for an OS user account with zero local profiles -- gets
        // this machine's up-to-date profile list immediately instead of waiting for the next
        // heartbeat cycle. See ConfigManager.SyncManagedProfiles / ClientRegistrationService.ResyncProfilesAsync.
        _ = SyncStatusAsync();
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(NotifyIcon))]
    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            _pollTimer.Stop();
            _ = ShowPopupAsync();
        }
    }

    private async Task ShowPopupAsync()
    {
        var cursor = Cursor.Position;

        // Not configured yet (fresh install, no installer-provided URL): the tray's job is to
        // collect the server address, so prompt for it instead of showing the (empty) popup —
        // unless the user already chose to run standalone, in which case the popup works normally.
        if (_serviceUp && _lastStatus.BackendUnconfigured && !_lastStatus.StandaloneMode)
        {
            _pollTimer.Start();
            await PromptSetupChoiceAsync();
            return;
        }

        if (_serviceUp)
            _popup.ShowAt(_lastStatus, cursor);
        else
            _popup.ShowServiceUnavailable(cursor);

        // Live refresh in background; popup updates when it returns
        await SyncStatusAsync();
    }

    // ── Status refresh ────────────────────────────────────────────────────────

    /// <summary>Live backend round-trip (CommandType.SyncStatus): registers, checks for updates,
    /// and resyncs this OS user's profiles against the machine's backend-authoritative set. Used
    /// on tray startup and whenever the popup opens. See constructor and <see cref="ShowPopupAsync"/>.</summary>
    private async Task SyncStatusAsync()
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.SyncStatus });
            if (!response.Success) { SetServiceUnavailable(); return; }
            var status = JsonSerializer.Deserialize(response.DataJson ?? "{}", ValeniusJsonContext.Default.TunnelStatus);
            if (status is null) return;
            _lastStatus = status;
            UpdateUi(status);
        }
        catch
        {
            SetServiceUnavailable();
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.Status });
            if (!response.Success) { SetServiceUnavailable(); return; }
            var status = JsonSerializer.Deserialize(response.DataJson ?? "{}", ValeniusJsonContext.Default.TunnelStatus);
            if (status is null) return;
            _lastStatus = status;
            UpdateUi(status);
        }
        catch
        {
            SetServiceUnavailable();
        }
    }

    private void UpdateUi(TunnelStatus status)
    {
        if (_anchor.InvokeRequired) { _anchor.BeginInvoke(() => UpdateUi(status)); return; }

        _serviceUp = true;

        // No backend URL configured yet and the user hasn't chosen standalone: show a
        // "Setup required" tray state and, on first discovery, auto-open the choice prompt once.
        // Re-prompting on demand happens when the user opens the popup (see ShowPopupAsync); the
        // background poll must not reopen the dialog on every tick.
        if (status.BackendUnconfigured && !status.StandaloneMode)
        {
            _trayIcon.Icon = _iconUnavailable;
            _trayIcon.Text = "Valenius - Setup required";
            _popup.RefreshStatus(status);
            if (!_backendPromptAutoShown && !_backendUrlOpen && !_setupChoiceOpen)
                _ = PromptSetupChoiceAsync();
            return;
        }

        // Standalone: no backend, but the user opted in — behaves exactly like a configured
        // client's UI (profile list, connect/upload/delete), just with a distinct idle tooltip.
        _trayIcon.Icon = status.IsConnected ? _iconConnected : _iconDisconnected;
        _trayIcon.Text = status.IsConnected
            ? $"Valenius - Connected ({status.TunnelName})"
            : status.BackendUnconfigured ? "Valenius - Standalone" : "Valenius - Disconnected";

        _popup.RefreshStatus(status);

        if (status.HasStagedConfig && !_claimingConfig)
        {
            _claimingConfig = true;
            _ = AutoClaimConfigAsync();
        }
    }

    private void SetServiceUnavailable()
    {
        if (_anchor.InvokeRequired) { _anchor.BeginInvoke(SetServiceUnavailable); return; }
        _serviceUp         = false;
        _trayIcon.Icon     = _iconUnavailable;
        _trayIcon.Text     = "Valenius - Service not running";
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async Task ConnectWithProfileAsync(string? profileName)
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand
            {
                Command     = CommandType.Connect,
                ProfileName = profileName
            });
            if (!response.Success && response.IsLanConflict)
                using (var form = new LanConflictForm(response.Error ?? "Unknown network conflict."))
                    form.ShowDialog();
            else if (!response.Success)
                ShowBalloon("Connect failed", response.Error ?? "Unknown error", ToolTipIcon.Error);
            else
                ShowBalloon("Valenius", "VPN connected.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Connect failed", ex.Message, ToolTipIcon.Error);
        }
        await RefreshStatusAsync();
    }

    private void OnAboutRequested()
    {
        if (_aboutForm is { IsDisposed: false })
        {
            _aboutForm.Activate();
            return;
        }
        _aboutForm = new AboutForm(_lastStatus.StandaloneMode)
        {
            SendLogsRequested        = () => _ = SendLogsAsync(),
            ConnectToServerRequested = () => _ = PromptBackendUrlAsync(),
        };
        _aboutForm.FormClosed += (_, _) => _aboutForm = null;
        _aboutForm.Show();
    }

    private async Task SendLogsAsync()
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.SendLogs });
            if (response.Success)
                ShowBalloon("Valenius", "Diagnostic logs are being sent to your administrator.", ToolTipIcon.Info);
            else
                ShowBalloon("Valenius", "Could not send logs — the Valenius service may be unavailable.", ToolTipIcon.Warning);
        }
        catch
        {
            ShowBalloon("Valenius", "Could not send diagnostic logs.", ToolTipIcon.Warning);
        }
    }

    // ── First-run backend URL setup ───────────────────────────────────────────

    /// <summary>
    /// First-run choice (fresh install with no installer-provided URL): connect to a Valenius
    /// server now, or use the client standalone. Single-instance via <see cref="_setupChoiceOpen"/>.
    /// On Cancel/close the client stays undecided and this reappears next popup open, same
    /// precedent as <see cref="PromptBackendUrlAsync"/>.
    /// </summary>
    private async Task PromptSetupChoiceAsync()
    {
        if (_setupChoiceOpen || _backendUrlOpen) return;
        _setupChoiceOpen        = true;
        _backendPromptAutoShown = true; // stop the background poll from queuing more prompts
        DialogResult choice;
        using (var form = new SetupChoiceForm())
            choice = form.ShowDialog();
        _setupChoiceOpen = false;

        if (choice == DialogResult.Yes) // "Connect to a server"
        {
            await PromptBackendUrlAsync();
            return;
        }

        if (choice == DialogResult.No) // "Use standalone"
        {
            try
            {
                var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.SetStandaloneMode });
                if (!response.Success)
                    ShowBalloon("Valenius", response.Error ?? "Could not switch to standalone mode.", ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowBalloon("Valenius", ex.Message, ToolTipIcon.Warning);
            }
        }

        await RefreshStatusAsync();
    }

    /// <summary>
    /// Prompts the user for the backend server DNS (fresh install with no installer-provided URL) and
    /// sends it to the service. Single-instance via <see cref="_backendUrlOpen"/>. On Cancel the client
    /// stays unconfigured and the prompt reappears the next time the user opens the tray popup.
    /// </summary>
    private async Task PromptBackendUrlAsync()
    {
        if (_backendUrlOpen) return;
        _backendUrlOpen         = true;
        _backendPromptAutoShown = true;   // stop the background poll from queuing more prompts
        try
        {
            using var form = new BackendUrlForm();
            while (form.ShowDialog() == DialogResult.OK)
            {
                var dns = form.EnteredDns;
                if (string.IsNullOrWhiteSpace(dns))
                {
                    form.SetError("Enter your server address, for example vpn.company.com.");
                    continue;
                }
                try
                {
                    var response = await _pipe.SendAsync(new PipeCommand
                    {
                        Command    = CommandType.SetBackendUrl,
                        BackendDns = dns
                    });
                    if (response.Success)
                    {
                        // Success may carry a non-fatal advisory (e.g. server not reachable yet).
                        if (!string.IsNullOrEmpty(response.Error))
                            ShowBalloon("Valenius", response.Error!, ToolTipIcon.Warning);
                        else
                            ShowBalloon("Valenius", "Server address saved. Connecting...", ToolTipIcon.Info);
                        break;
                    }
                    form.SetError(response.Error ?? "Could not save the server address.");
                }
                catch (Exception ex)
                {
                    form.SetError(ex.Message);
                }
            }
        }
        finally
        {
            _backendUrlOpen = false;
        }
        await RefreshStatusAsync();
    }

    // ── MFA session gating ────────────────────────────────────────────────────

    /// <summary>Opens the MFA authorization deep link in the user's default browser.</summary>
    private void OnMfaAuthorize(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            ShowBalloon("Valenius", "Complete authorization in your browser, then connect.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not open browser", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>Shows the enrollment dialog and confirms the code with the service.</summary>
    private async Task OnMfaEnrollAsync(string otpAuthUri)
    {
        if (_mfaEnrollOpen) return;
        _mfaEnrollOpen = true;
        try
        {
            using var form = new MfaEnrollForm(otpAuthUri);
            while (form.ShowDialog() == DialogResult.OK)
            {
                if (form.EnteredCode.Length != 6)
                {
                    form.SetError("Enter the 6-digit code from your authenticator app.");
                    continue;
                }
                try
                {
                    var response = await _pipe.SendAsync(new PipeCommand
                    {
                        Command = CommandType.MfaEnrollConfirm,
                        MfaCode = form.EnteredCode
                    });
                    if (response.Success)
                    {
                        ShowBalloon("Valenius", "Two-factor authentication is set up.", ToolTipIcon.Info);
                        break;
                    }
                    form.SetError(response.Error ?? "That code was not accepted. Please try again.");
                }
                catch (Exception ex)
                {
                    form.SetError(ex.Message);
                }
            }
        }
        finally
        {
            _mfaEnrollOpen = false;
        }
        await RefreshStatusAsync();
    }

    private async Task OnDisconnect(string profileName)
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand
            {
                Command     = CommandType.Disconnect,
                ProfileName = profileName
            });
            if (!response.Success)
                ShowBalloon("Disconnect failed", response.Error ?? "Unknown error", ToolTipIcon.Error);
            else
                ShowBalloon("Valenius", $"'{profileName}' disconnected.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Disconnect failed", ex.Message, ToolTipIcon.Error);
        }
        await RefreshStatusAsync();
    }

    private async Task OnDisconnectAll()
    {
        try
        {
            foreach (var tunnel in _lastStatus.ConnectedTunnels)
            {
                var response = await _pipe.SendAsync(new PipeCommand
                {
                    Command     = CommandType.Disconnect,
                    ProfileName = tunnel.Name
                });
                if (!response.Success)
                    ShowBalloon("Disconnect failed",
                        $"{tunnel.Name}: {response.Error ?? "Unknown error"}", ToolTipIcon.Error);
            }
            ShowBalloon("Valenius", "All tunnels disconnected.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Disconnect failed", ex.Message, ToolTipIcon.Error);
        }
        await RefreshStatusAsync();
    }

    private async void OnUploadConfig()
    {
        if (_uploadConfigOpen) return;
        _uploadConfigOpen = true;
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title           = "Select WireGuard config",
                Filter          = "WireGuard config (*.conf)|*.conf",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            var defaultName = Valenius.Shared.ProfileNameHelper.Sanitize(dialog.FileName);
            var prompted = PromptProfileName(defaultName);
            if (prompted is null) return;
            var (profileName, shareAllUsers) = prompted.Value;

            if (!Valenius.Shared.ProfileNameHelper.IsValid(profileName))
            {
                ShowBalloon("Upload failed",
                    "Profile name may only contain letters, digits, underscores, and hyphens (max 50 chars).",
                    ToolTipIcon.Error);
                return;
            }

            try
            {
                var content  = await File.ReadAllTextAsync(dialog.FileName);
                var response = await _pipe.SendAsync(new PipeCommand
                {
                    Command           = CommandType.UploadConfig,
                    ConfigContent     = content,
                    ProfileName       = profileName,
                    ShareWithAllUsers = shareAllUsers
                });

                if (!response.Success)
                    ShowBalloon("Upload failed", response.Error ?? "Unknown error", ToolTipIcon.Error);
                else if (response.ShareFailed)
                    ShowBalloon("Valenius", response.Error ?? $"Profile '{profileName}' saved.", ToolTipIcon.Warning);
                else
                    ShowBalloon("Valenius", $"Profile '{profileName}' saved.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ShowBalloon("Upload failed", ex.Message, ToolTipIcon.Error);
            }
        }
        finally
        {
            _uploadConfigOpen = false;
        }
    }

    private async Task OnDeleteProfileAsync(string profileName)
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand
            {
                Command     = CommandType.DeleteProfile,
                ProfileName = profileName
            });
            if (!response.Success)
                ShowBalloon("Delete failed", response.Error ?? "Unknown error", ToolTipIcon.Error);
            else
                ShowBalloon("Valenius", $"Profile '{profileName}' deleted.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Delete failed", ex.Message, ToolTipIcon.Error);
        }
        await RefreshStatusAsync();
    }

    private async Task OnAutoConnectToggle()
    {
        bool newValue = !_lastStatus.AutoConnectEnabled;
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand
            {
                Command            = CommandType.SetAutoConnect,
                AutoConnectEnabled = newValue
            });
            if (!response.Success)
                ShowBalloon("Auto Connect", response.Error ?? "Failed to update setting.", ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowBalloon("Auto Connect", ex.Message, ToolTipIcon.Warning);
        }
        await RefreshStatusAsync();
    }

    private async Task OnInstallUpdate()
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.CheckUpdate });
            if (!response.Success) { ShowBalloon("Update failed", response.Error ?? "Unknown error", ToolTipIcon.Warning); return; }
            var result = JsonSerializer.Deserialize(response.DataJson ?? "{}", ValeniusJsonContext.Default.VersionCheckResult);
            if (result?.UpdateAvailable == true)
                ShowBalloon("Valenius",
                    $"Version {result.LatestVersion} is downloading. The app will restart automatically.",
                    ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Update failed", ex.Message, ToolTipIcon.Warning);
        }
    }

    private async Task OnRegisterClient()
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.Register });
            if (!response.Success) { ShowBalloon("Registration failed", response.Error ?? "Unknown error", ToolTipIcon.Error); return; }
            var result = JsonSerializer.Deserialize(response.DataJson ?? "{}", ValeniusJsonContext.Default.RegistrationResult);
            if (result is null) return;
            ShowBalloon("Valenius",
                result.Message ?? (result.IsActive ? "Active." : "Pending activation."),
                result.IsActive ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowBalloon("Registration failed", ex.Message, ToolTipIcon.Error);
        }
        await RefreshStatusAsync();
    }

    private async Task AutoClaimConfigAsync()
    {
        try
        {
            var response = await _pipe.SendAsync(new PipeCommand { Command = CommandType.GetConfigInfo });
            if (response.Success)
            {
                var info = JsonSerializer.Deserialize(response.DataJson ?? "{}", ValeniusJsonContext.Default.ConfigInfo);
                if (info?.HasConfig == true)
                    ShowBalloon("Valenius", $"Config installed: {info.TunnelName}", ToolTipIcon.Info);
            }
            else
            {
                ShowBalloon("Config install failed", response.Error ?? "Unknown error", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Config install failed", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _claimingConfig = false;
        }
        await RefreshStatusAsync();
    }

    private void OnExit()
    {
        _pollTimer.Stop();
        _trayIcon.Visible = false;
        // Tell the service we're going offline. Block briefly (max 1 s) so the
        // notification reaches the service before this process exits.
        try { _pipe.SendAsync(new PipeCommand { Command = CommandType.NotifyOffline }).Wait(1000); }
        catch { /* best effort — don't block the exit on pipe failure */ }
        Application.Exit();
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon) =>
        _trayIcon.ShowBalloonTip(4000, title, text, icon);

    // ── Profile name prompt ───────────────────────────────────────────────────

    private static (string Name, bool ShareAllUsers)? PromptProfileName(string defaultName)
    {
        var label     = new Label    { Text = "Enter a name for this VPN profile:", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        var textBox   = new TextBox  { Text = defaultName, MinimumSize = new Size(300, 0), Margin = new Padding(0, 0, 0, 10) };
        var shareBox  = new CheckBox { Text = "Available to all users on this PC", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        var okBtn     = new Button   { Text = "OK",     DialogResult = DialogResult.OK,     AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancelBtn = new Button   { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(6, 0, 0, 0) };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Anchor        = AnchorStyles.Right,
            Margin        = new Padding(0)
        };
        buttons.Controls.Add(cancelBtn);
        buttons.Controls.Add(okBtn);

        var table = new TableLayoutPanel
        {
            ColumnCount  = 1,
            RowCount     = 4,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(12),
            Margin       = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label,    0, 0);
        table.Controls.Add(textBox,  0, 1);
        table.Controls.Add(shareBox, 0, 2);
        table.Controls.Add(buttons,  0, 3);

        using var form = new Form
        {
            Text                = "Profile name",
            StartPosition       = FormStartPosition.CenterScreen,
            FormBorderStyle     = FormBorderStyle.FixedDialog,
            MaximizeBox         = false,
            MinimizeBox         = false,
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode       = AutoScaleMode.Dpi,
            AutoSize            = true,
            AutoSizeMode        = AutoSizeMode.GrowAndShrink,
            AcceptButton        = okBtn,
            CancelButton        = cancelBtn
        };
        form.Controls.Add(table);
        return form.ShowDialog() == DialogResult.OK ? (textBox.Text.Trim(), shareBox.Checked) : null;
    }

    // ── Icon construction ─────────────────────────────────────────────────────

    private static Icon BuildUnavailableIcon()
    {
        const int Size = 32;
        using var bmp  = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g    = Graphics.FromImage(bmp);

        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var cm = new ColorMatrix(new[]
        {
            new float[] { 0.30f, 0.30f, 0.30f, 0, 0 },
            new float[] { 0.59f, 0.59f, 0.59f, 0, 0 },
            new float[] { 0.11f, 0.11f, 0.11f, 0, 0 },
            new float[] { 0,     0,     0,     1, 0 },
            new float[] { 0,     0,     0,     0, 1 }
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(cm);
        g.DrawImage(_appImage,
            new Rectangle(0, 0, Size, Size),
            0, 0, _appImage.Width, _appImage.Height,
            GraphicsUnit.Pixel, attrs);

        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private static Image LoadEmbeddedImage(string fileName)
    {
        var asm  = Assembly.GetExecutingAssembly();
        var name = $"Valenius.TrayApp.Resources.{fileName}";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        return Image.FromStream(stream);
    }

    private static Icon LoadEmbeddedIcon(string fileName)
    {
        var asm  = Assembly.GetExecutingAssembly();
        var name = $"Valenius.TrayApp.Resources.{fileName}";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        return new Icon(stream);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Dispose();
            _trayIcon.Dispose();
            _popup.Dispose();
            _anchor.Dispose();
            _iconConnected.Dispose();
            _iconDisconnected.Dispose();
            _iconUnavailable.Dispose();
        }
        base.Dispose(disposing);
    }
}
