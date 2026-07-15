namespace Valenius.Service;

/// <summary>
/// Runs once at service startup and launches the TrayApp in the active user session
/// if it is not already running.
///
/// This handles the auto-update scenario: the installer (running as SYSTEM from the
/// service) cannot directly start a GUI in the user's desktop session.  When the new
/// service starts after an update the TrayApp is gone — this service restarts it.
///
/// On a normal machine restart the HKLM Run key already handles TrayApp startup at
/// user login, so this service is usually a no-op in that case.
/// </summary>
public class TrayLaunchService(IConfiguration config, ILogger<TrayLaunchService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Attempt to launch the TrayApp up to three times with increasing delays.
        // Multiple attempts handle:
        //   - install-service.ps1 still running when the first check fires
        //   - User session initialising slowly after an OS update/restart
        //   - Transient WTS API failures on the first attempt
        int[] retryDelaysSeconds = { 10, 20, 30 };

        foreach (var delay in retryDelaysSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            if (stoppingToken.IsCancellationRequested) return;

            // Another mechanism (install-service.ps1 or HKLM Run) may have already started it.
            if (IsTrayRunning())
            {
                logger.LogDebug("TrayApp already running after {Delay}s — skipping launch.", delay);
                return;
            }

            var trayPath = ResolveTrayPath();
            if (!File.Exists(trayPath))
            {
                logger.LogWarning("TrayApp not found at {Path}.", trayPath);
                return;
            }

            try
            {
                bool launched = SessionLauncher.LaunchInUserSession(trayPath);
                if (launched)
                {
                    logger.LogInformation("TrayApp launched in user session (attempt at +{Delay}s).", delay);
                    return;
                }
                // false = no interactive user session yet (login screen, early boot, etc.)
                logger.LogDebug("No active user session at +{Delay}s — will retry.", delay);
            }
            catch (Exception ex)
            {
                // Unexpected WTS failure — log at debug so it doesn't pollute event log on
                // every service start; the retries at +30s and +60s handle transient cases.
                logger.LogDebug(ex, "TrayApp launch attempt at +{Delay}s failed — will retry.", delay);
            }
        }

        logger.LogWarning(
            "TrayApp could not be launched automatically after update. " +
            "The user may need to start it manually or it will open on next login.");
    }

    private static bool IsTrayRunning() =>
        System.Diagnostics.Process.GetProcessesByName("Valenius.TrayApp").Any();

    private string ResolveTrayPath()
    {
        // Service EXE lives at: {install}\service\Valenius.Service.exe
        // TrayApp EXE lives at: {install}\trayapp\Valenius.TrayApp.exe
        var serviceExe = Environment.ProcessPath ?? string.Empty;
        var serviceDir = Path.GetDirectoryName(serviceExe) ?? AppContext.BaseDirectory;
        var installDir = Path.GetDirectoryName(serviceDir) ?? serviceDir;

        // Allow override via appsettings (useful in dev).
        var configured = config["Valenius:TrayAppPath"];
        if (!string.IsNullOrEmpty(configured)) return configured;

        return Path.Combine(installDir, "trayapp", "Valenius.TrayApp.exe");
    }
}
