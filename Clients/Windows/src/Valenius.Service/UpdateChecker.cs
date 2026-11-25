using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using Valenius.Shared;

namespace Valenius.Service;

public class UpdateChecker : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly WireGuardController _wg;
    private readonly TunnelStateManager _state;
    private readonly RegistrationManager _registration;
    private readonly BackendUrlProvider _backendUrl;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private volatile VersionCheckResult? _lastResult;

    /// <summary>Old-installer paths we've already warned about failing to delete — logged once per
    /// path (not every hourly cycle) so a persistently stuck file doesn't spam the Event Log
    /// forever. Cleared when a path is successfully deleted, so a future recurrence warns again.</summary>
    private readonly HashSet<string> _warnedStuckInstallers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string DefaultUpdatesDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Valenius", "updates");
    private readonly string UpdatesDir;
    private readonly Action<string> _launchInstaller;
    private readonly Func<CancellationToken, Task> _disconnectTunnels;
    private readonly Action<string, string> _runProcess;

    public UpdateChecker(IHttpClientFactory httpFactory, ILogger<UpdateChecker> logger,
        WireGuardController wg, TunnelStateManager state, RegistrationManager registration, BackendUrlProvider backendUrl)
        : this(httpFactory, logger, wg, state, registration, backendUrl, DefaultUpdatesDir) { }

    internal UpdateChecker(IHttpClientFactory httpFactory, ILogger<UpdateChecker> logger,
        WireGuardController wg, TunnelStateManager state, RegistrationManager registration, BackendUrlProvider backendUrl,
        string updatesDir, Action<string>? launchInstaller = null, Func<CancellationToken, Task>? disconnectTunnels = null,
        Action<string, string>? runProcess = null)
    {
        _httpFactory  = httpFactory;
        _logger       = logger;
        _wg           = wg;
        _state        = state;
        _registration = registration;
        _backendUrl   = backendUrl;
        UpdatesDir = updatesDir;
        _launchInstaller = launchInstaller ?? LaunchInstallerDetached;
        _disconnectTunnels = disconnectTunnels ?? DisconnectRunningTunnelsAsync;
        _runProcess = runProcess ?? RunProcess;
        Directory.CreateDirectory(UpdatesDir);
    }

    private HttpClient CreateHttp() => _httpFactory.CreateClient("Update");

    /// <summary>The version-manifest URL for this client's channel. Stable adds no query param
    /// (byte-compatible with older backends); beta requests …?channel=beta (falls back to stable).</summary>
    private string VersionUrl(string backendUrl) =>
        _registration.GetUpdateChannel() == "beta"
            ? $"{backendUrl}/api/version?channel=beta"
            : $"{backendUrl}/api/version";

    /// <summary>
    /// The base URL to poll for updates: the configured backend when there is one, otherwise
    /// <see cref="CloudUpdateSource"/> for a client that explicitly chose standalone mode (no
    /// backend at all — without this fallback such a client would never update). Returns null
    /// when neither applies (unconfigured and not standalone — mid first-run setup).
    /// </summary>
    private string? EffectiveUpdateBaseUrl()
    {
        var backendUrl = _backendUrl.Current;
        if (!string.IsNullOrEmpty(backendUrl)) return backendUrl;
        return _registration.GetStandaloneModeChosen() ? CloudUpdateSource.BaseUrl : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial check after a short delay so the service fully starts first
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAndApplyUpdateAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    public async Task<VersionCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var backendUrl = EffectiveUpdateBaseUrl();
        if (backendUrl is null)
            return new VersionCheckResult { CurrentVersion = GetCurrentVersion() };

        using var http = CreateHttp();
        var manifest = await http.GetFromJsonAsync(VersionUrl(backendUrl), ValeniusJsonContext.Default.VersionManifest, ct);
        if (manifest is null)
            throw new InvalidOperationException("Server returned an empty version manifest.");

        var current = GetCurrentVersion();
        _lastResult = new VersionCheckResult
        {
            CurrentVersion   = current,
            LatestVersion    = manifest.Version,
            UpdateAvailable  = IsNewer(manifest.Version, current),
            ReleaseNotes     = manifest.ReleaseNotes
        };
        return _lastResult;
    }

    public VersionCheckResult? GetCachedResult() => _lastResult;

    public async Task CheckAndApplyUpdateAsync(CancellationToken ct = default)
    {
        if (!await _downloadLock.WaitAsync(0, ct))
        {
            _logger.LogDebug("Update download already in progress, skipping.");
            return;
        }
        try
        {
        var backendUrl = EffectiveUpdateBaseUrl();
        if (backendUrl is null)
            return;

        try
        {
            using var http = CreateHttp();
            var manifest = await http.GetFromJsonAsync(VersionUrl(backendUrl), ValeniusJsonContext.Default.VersionManifest, ct);
            if (manifest is null || !IsNewer(manifest.Version, GetCurrentVersion()))
                return;

            var installerPath = Path.Combine(UpdatesDir, $"ValeniusSetup-{manifest.Version}.exe");

            // Always verify the hash — even for a cached file — so a tampered copy on
            // disk is never executed as SYSTEM.
            if (File.Exists(installerPath))
            {
                using var cached = File.OpenRead(installerPath);
                if (cached.Length > 512L * 1024 * 1024) throw new InvalidDataException("Cached update exceeds 512 MiB.");
                var cachedHash = Convert.ToHexString(await SHA256.HashDataAsync(cached, ct)).ToLowerInvariant();
                cached.Close();
                if (!cachedHash.Equals(manifest.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    _logger.LogWarning("Cached installer hash mismatch — re-downloading. Expected {Expected}, got {Actual}", manifest.Sha256, cachedHash);
                    File.Delete(installerPath);
                }
                else
                {
                    _logger.LogInformation("Installer already present with matching hash: {Path}", installerPath);
                }
            }

            if (!File.Exists(installerPath))
            {
                _logger.LogInformation("Downloading update {Version}...", manifest.Version);
                // DownloadUrl may be absolute (written by the Releases page) or relative.
                var downloadUrl = Uri.IsWellFormedUriString(manifest.DownloadUrl, UriKind.Absolute)
                    ? manifest.DownloadUrl
                    : $"{backendUrl}{manifest.DownloadUrl}";
                var temporary = installerPath + ".partial";
                try
                {
                    using var downloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    downloadDeadline.CancelAfter(TimeSpan.FromMinutes(5));
                    var downloadToken = downloadDeadline.Token;
                    using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadToken);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 512L * 1024 * 1024)
                        throw new InvalidDataException("Update exceeds 512 MiB.");
                    await using (var input = await response.Content.ReadAsStreamAsync(downloadToken))
                    await using (var output = File.Create(temporary))
                        await CopyBoundedAsync(input, output, 512L * 1024 * 1024, downloadToken);
                    using var downloaded = File.OpenRead(temporary);
                    var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct)).ToLowerInvariant();
                    if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Update hash mismatch.");
                    downloaded.Close();
                    File.Move(temporary, installerPath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                _logger.LogInformation("Update downloaded with matching hash: {Path}", installerPath);
            }

            // Backend-provided hashes alone are not publisher authorization. This gate
            // applies equally to downloads and cached files, before any disruptive action.
            using var verified = VerifiedUpdateInstaller.Open(installerPath, manifest.Sha256,
                manifest.Version, Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? GetCurrentVersion());
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Installer signature, publisher and signed release version verified: {Path}", installerPath);

            // Delete older installers before launching — the service will be killed
            // by the installer shortly after, so this is the last chance to clean up.
            foreach (var old in Directory.EnumerateFiles(UpdatesDir, "*.exe")
                .Where(f => !string.Equals(f, installerPath, StringComparison.OrdinalIgnoreCase)))
            {
                TryDeleteOldInstaller(old);
            }

            // Disconnect all active WireGuard tunnels before the installer runs.
            // wireguard.exe is bundled in the installer and cannot be overwritten while
            // any WireGuardTunnel$ service holds it locked.
            // The installer's CurStepChanged also does this as a safety net.
            await _disconnectTunnels(ct);
            _launchInstaller(installerPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-update failed");
        }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    internal static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            received += count;
            if (received > maximum) throw new InvalidDataException("Download exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    private async Task DisconnectRunningTunnelsAsync(CancellationToken ct)
    {
        try
        {
            var running = _wg.GetRunningTunnels().ToList();
            foreach (var tunnelName in running)
            {
                _logger.LogInformation("Update: stopping WireGuard tunnel '{Tunnel}'.", tunnelName);
                await _wg.DisconnectAsync(tunnelName);
            }
            if (running.Count > 0)
            {
                _state.SetDisconnected();
                _logger.LogInformation("Update: all tunnels disconnected ({Count}). Launching installer.", running.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update: could not fully disconnect VPN tunnels; installer will handle it.");
        }
    }

    /// <summary>
    /// Sweeps every leftover installer .exe under updates\ and retries deleting each — unlike the
    /// cleanup inside <see cref="CheckAndApplyUpdateAsync"/>, this runs on demand regardless of
    /// whether a newer version is currently available, so a file a permission fix just unblocked
    /// (e.g. via "Repair client config") gets a fresh delete attempt immediately instead of
    /// waiting for the next real update to happen to trigger cleanup as a side effect.
    /// </summary>
    public void CleanupStaleInstallers()
    {
        foreach (var old in Directory.EnumerateFiles(UpdatesDir, "*.exe"))
            TryDeleteOldInstaller(old);
    }

    /// <summary>
    /// Best-effort delete of a leftover installer .exe. On failure: first clears a ReadOnly
    /// attribute if set (the same File.Delete "Access is denied" message covers both a genuine
    /// ACL denial and a read-only-attributed file — AV products, e.g. Bitdefender per
    /// StateFileWriter's doc comment, are known to leave files under this tree read-only), then
    /// falls back to the safe (non-invasive, DACL-only — never the ownership-takeover escalation,
    /// which stays opt-in via the admin "Repair client config" action) self-heal and retries.
    /// Still-stuck files are warned about only once (not every hourly cycle) via
    /// <see cref="_warnedStuckInstallers"/> — they'll be caught for real by an admin running
    /// "Repair client config", or the next automatic startup self-heal.
    /// </summary>
    private void TryDeleteOldInstaller(string path)
    {
        try
        {
            ClearReadOnlyIfSet(path);
            File.Delete(path);
            _warnedStuckInstallers.Remove(path);
            return;
        }
        catch (Exception ex)
        {
            DataDirSelfHeal.EnsureAccessible(DataDirHealthService.DataDir, _logger);
            try
            {
                ClearReadOnlyIfSet(path);
                File.Delete(path);
                _warnedStuckInstallers.Remove(path);
                _logger.LogInformation("Deleted old installer {Path} after a self-heal retry.", path);
            }
            catch (Exception ex2)
            {
                if (_warnedStuckInstallers.Add(path))
                    _logger.LogWarning(ex2, "Could not delete old installer {Path} (first exception: {FirstError}).", path, ex.Message);
            }
        }
    }

    /// <summary>Clears FileAttributes.ReadOnly if set. Same helper as CleanupOldLaunchScripts uses
    /// for run-update*.cmd — File.Delete/File.Open throw the identical "Access is denied"
    /// UnauthorizedAccessException for a read-only-attributed file as for a genuine ACL denial, and
    /// AV products are known to leave files under this tree read-only (see StateFileWriter).</summary>
    private static void ClearReadOnlyIfSet(string path)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
    }

    private const string UpdateTaskName = "ValeniusAutoUpdate";

    /// <summary>
    /// Launches the installer DETACHED from this service via a one-shot Scheduled Task.
    ///
    /// The installer stops the Valenius service mid-install (Valenius.iss CurStepChanged,
    /// before any files are copied). If it runs as a direct child of this service, that
    /// stop tears the installer down before it can finish — so the auto-update silently
    /// fails even though a manual run succeeds (a hand-run installer's parent is explorer,
    /// not the service, so stopping the service does not affect it). Task Scheduler owns
    /// the launched process, so it survives the service being stopped and restarted.
    ///
    /// The heavy quoting (installer path + args + task self-delete) is kept inside a small
    /// .cmd script so we avoid schtasks /TR escaping pitfalls. The script also passes
    /// /LOG so a failed silent install leaves a diagnosable trail, and deletes the task
    /// after the installer exits.
    /// </summary>
    internal void LaunchInstallerDetached(string installerPath) =>
        ServiceFileAccess.Run(() => LaunchInstallerDetachedCore(installerPath));

    private void LaunchInstallerDetachedCore(string installerPath)
    {
        // Write a UNIQUELY-NAMED launch script each run rather than overwriting a fixed
        // run-update.cmd. A leftover copy can be read-only, locked by a still-running task,
        // or carry a hostile/empty DACL that even SYSTEM cannot rewrite (the empty-DACL trap) — any of which makes File.WriteAllText
        // throw UnauthorizedAccessException and strands the client on its current version. A
        // fresh name never touches the poisoned file, so the write always succeeds.
        CleanupOldLaunchScripts();

        var cmdPath = Path.Combine(UpdatesDir, $"run-update-{DateTime.UtcNow:yyyyMMddHHmmssfff}.cmd");
        var logPath = Path.Combine(UpdatesDir, "install.log");
        var script =
            "@echo off\r\n" +
            $"\"{installerPath}\" /SILENT /NORESTART /LOG=\"{logPath}\"\r\n" +
            $"schtasks /Delete /TN {UpdateTaskName} /F\r\n" +
            "del /f /q \"%~f0\"\r\n";   // one-shot: self-delete the launch script when done
        ServiceFileAccess.WritePrivateText(cmdPath, script);

        // schtasks /Create's default battery restrictions prevent laptop updates.
        // Import an explicit demand-start definition; keep the installer detached
        // as SYSTEM and allow both starting and continuing on battery power.
        var taskXmlPath = cmdPath + ".xml";
        try
        {
            ServiceFileAccess.WritePrivateText(taskXmlPath, UpdateTaskDefinition.Create(cmdPath));
            _runProcess("schtasks.exe", $"/Create /TN {UpdateTaskName} /XML \"{taskXmlPath}\" /F");
            _runProcess("schtasks.exe", $"/Run /TN {UpdateTaskName}");
            _logger.LogInformation("Update: scheduler accepted installer task '{Task}' (battery power allowed).", UpdateTaskName);
        }
        finally
        {
            try { File.Delete(taskXmlPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Best-effort removal of launch scripts from earlier runs (including a legacy fixed-name
    /// <c>run-update.cmd</c>). Clears a read-only attribute first; a script still locked by a
    /// running task, or poisoned with a DACL that denies SYSTEM, is skipped and simply left
    /// behind (harmless — the next launch uses a fresh name regardless).
    /// </summary>
    private void CleanupOldLaunchScripts()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(UpdatesDir, "run-update*.cmd"))
            {
                try
                {
                    ClearReadOnlyIfSet(old);
                    File.Delete(old);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not remove old launch script {Path}", old); }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not enumerate old launch scripts."); }
    }

    private void RunProcess(string fileName, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null)
            throw new InvalidOperationException($"Update: could not start {fileName}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15_000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"Update: {fileName} timed out.");
        }
        if (!Task.WhenAll(new Task[] { stdout, stderr }).Wait(15_000))
            throw new TimeoutException($"Update: could not finish reading {fileName} output.");
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Update: {fileName} exited {p.ExitCode}. " +
                stdout.GetAwaiter().GetResult().Trim() + " " + stderr.GetAwaiter().GetResult().Trim());
    }

    public static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(candidate, out var v1) && Version.TryParse(current, out var v2))
            return v1 > v2;
        return false;
    }
}
