using System.Diagnostics;

namespace Valenius.Service;

/// <summary>
/// Restarts the Valenius Windows service itself, on admin request from the backend
/// (<c>StatusResponse.ServiceRestartRequested</c> → <see cref="ClientRegistrationService"/>).
///
/// Recovery hatch for stale in-memory state: <see cref="TunnelStateManager"/> lives in this
/// process only, so a tunnel that disappears behind the service's back leaves it reporting
/// Connected with no tunnel present — the tray then offers only Disconnect, which fails, and the
/// user can't get back to a working tunnel without local admin rights. A restart clears that.
///
/// **The restart must be driven from outside this process**, for the same reason
/// <see cref="UpdateChecker.LaunchInstallerDetached"/> uses a scheduled task: the script stops the
/// very service that started it, so anything running as our own child is torn down mid-way and the
/// service never comes back up. A one-shot Task Scheduler task is owned by the scheduler, survives
/// our stop, and starts us again.
/// </summary>
public class ServiceRestarter
{
    public const string WindowsServiceName = "Valenius";

    private const string RestartTaskName = "ValeniusServiceRestart";

    private static readonly string ScriptDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "Valenius", "updates");

    private readonly ILogger<ServiceRestarter> _logger;

    public ServiceRestarter(ILogger<ServiceRestarter> logger) => _logger = logger;

    /// <summary>
    /// Schedules a restart of this service a few seconds from now. Returns once the task is
    /// queued — the caller keeps running until the scheduler stops us, which is what lets the
    /// heartbeat response that carried the request finish being processed (and the one-shot flag
    /// stay cleared server-side) before we go down.
    /// </summary>
    public void RequestRestart()
    {
        try
        {
            Directory.CreateDirectory(ScriptDir);
            CleanupOldScripts();

            // Uniquely-named per run — never a fixed name. A leftover copy can be read-only,
            // locked, or carry a hostile/empty DACL that even SYSTEM cannot rewrite (the
            // empty-DACL trap), which would make the write throw and strand the restart.
            var cmdPath = Path.Combine(ScriptDir, $"restart-service-{DateTime.UtcNow:yyyyMMddHHmmssfff}.cmd");
            // `net stop` blocks until the service is actually stopped, so the following `net start`
            // can't race a still-stopping service. `ping` rather than `timeout` for the pauses:
            // timeout.exe aborts with "input redirection is not supported" when it has no usable
            // console, which is exactly how a scheduled task can run.
            var script =
                "@echo off\r\n" +
                // Give the caller a moment to finish the heartbeat that requested this.
                "ping -n 4 127.0.0.1 >nul 2>&1\r\n" +
                $"net stop {WindowsServiceName} >nul 2>&1\r\n" +
                "ping -n 3 127.0.0.1 >nul 2>&1\r\n" +
                $"net start {WindowsServiceName} >nul 2>&1\r\n" +
                $"schtasks /Delete /TN {RestartTaskName} /F >nul 2>&1\r\n" +
                "del /f /q \"%~f0\"\r\n";   // one-shot: self-delete when done
            File.WriteAllText(cmdPath, script);

            // ScriptDir lives under C:\ProgramData (no spaces), so a single quote layer is enough.
            RunProcess("schtasks.exe",
                $"/Create /TN {RestartTaskName} /TR \"{cmdPath}\" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F");
            RunProcess("schtasks.exe", $"/Run /TN {RestartTaskName}");
            _logger.LogInformation("Service restart scheduled via task '{Task}'.", RestartTaskName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not schedule a service restart.");
        }
    }

    /// <summary>Best-effort removal of restart scripts left behind by earlier runs. A script still
    /// locked by a running task is skipped — harmless, since the next run uses a fresh name.</summary>
    private void CleanupOldScripts()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(ScriptDir, "restart-service*.cmd"))
            {
                try
                {
                    var attr = File.GetAttributes(old);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(old, attr & ~FileAttributes.ReadOnly);
                    File.Delete(old);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not remove old restart script {Path}", old); }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not enumerate old restart scripts."); }
    }

    private void RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            _logger.LogWarning("Service restart: could not start {File}.", fileName);
            return;
        }
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            _logger.LogWarning("Service restart: {File} {Args} exited {Code}. {Out} {Err}",
                fileName, arguments, p.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
