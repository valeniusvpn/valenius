using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;

namespace Valenius.Service;

public class WireGuardController
{
    // wireguard.exe is bundled in the same directory as the service executable.
    private static readonly string WireGuardExe =
        Path.Combine(AppContext.BaseDirectory, "wireguard.exe");

    private readonly ConfigManager _configs;
    private readonly ILogger<WireGuardController> _logger;

    public WireGuardController(ConfigManager configs, ILogger<WireGuardController> logger)
    {
        _configs = configs;
        _logger  = logger;
    }

    public bool IsWireGuardInstalled() => File.Exists(WireGuardExe);

    public async Task<(bool Success, string Output)> ConnectAsync(string configPath)
    {
        // Decrypt to a SYSTEM-only temp file if the stored config is DPAPI-encrypted.
        var wireGuardPath = _configs.PrepareConfigPath(configPath);

        var result = await RunAsync($"/installtunnelservice \"{wireGuardPath}\"");
        if (result.Success)
        {
            // wireguard.exe sets tunnel services to Automatic start by default.
            // Change to Manual immediately so the tunnel never auto-starts after a reboot.
            var tunnelName = Path.GetFileNameWithoutExtension(wireGuardPath);
            await SetStartTypeManualAsync($"WireGuardTunnel${tunnelName}");
        }
        return result;
    }

    private async Task SetStartTypeManualAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "sc.exe",
                Arguments       = $"config \"{serviceName}\" start= demand",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow  = true
            };
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                _logger.LogWarning("sc.exe config {Service} start= demand failed (exit {Code})", serviceName, process.ExitCode);
            else
                _logger.LogInformation("Tunnel service {Service} set to Manual start.", serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set {Service} startup type to Manual.", serviceName);
        }
    }

    public async Task<(bool Success, string Output)> DisconnectAsync(string tunnelName)
    {
        var result = await RunAsync($"/uninstalltunnelservice \"{tunnelName}\"");
        // Delete any temp plaintext file that was created for an encrypted profile.
        _configs.CleanupTempConfig(tunnelName);
        return result;
    }

    public bool IsTunnelServiceRunning(string tunnelName)
    {
        try
        {
            using var sc = new ServiceController($"WireGuardTunnel${tunnelName}");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<string> GetRunningTunnels()
    {
        return ServiceController.GetServices()
            .Where(s => s.ServiceName.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ServiceName["WireGuardTunnel$".Length..]);
    }

    // ── AllowedIPs conflict detection ─────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable description of the first AllowedIPs conflict between the
    /// config at <paramref name="newConfigPath"/> and any already-connected tunnel, or null
    /// if there is none. A full-tunnel (0.0.0.0/0 or ::/0) config can never coexist with
    /// another tunnel and is reported specially.
    /// </summary>
    public string? FindAllowedIpsConflict(string newConfigPath, IEnumerable<string> activeTunnelNames)
    {
        var newCidrs = _configs.ReadAllowedIPs(newConfigPath);
        if (newCidrs.Length == 0) return null;   // fail-open: couldn't read the new config

        var newName = ConfigManager.GetTunnelNameFromPath(newConfigPath) ?? "this profile";
        bool newIsFull = newCidrs.Any(IsDefaultRoute);

        foreach (var activeName in activeTunnelNames)
        {
            var activePath = _configs.GetAnyConfigPath(activeName);
            if (activePath is null) continue;
            var activeCidrs = _configs.ReadAllowedIPs(activePath);
            if (activeCidrs.Length == 0) continue;

            bool activeIsFull = activeCidrs.Any(IsDefaultRoute);
            if (newIsFull)
                return $"Profile '{newName}' routes all traffic (0.0.0.0/0) — disconnect it before connecting another profile.";
            if (activeIsFull)
                return $"Profile '{activeName}' routes all traffic (0.0.0.0/0) — disconnect it before connecting another profile.";

            foreach (var a in newCidrs)
                foreach (var b in activeCidrs)
                    if (CidrsOverlap(a, b))
                        return $"Profile '{newName}' ({a}) conflicts with the already-connected '{activeName}' ({b}).";
        }

        return null;
    }

    // ── Local LAN conflict detection ──────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable description of a conflict between this machine's own local LAN
    /// and either (A) an explicit remote route in the new config's AllowedIPs, (B) a remote LAN
    /// CIDR reported by the backend for a full-tunnel profile (<paramref name="statusRemoteLanCidrs"/>),
    /// or (C) the VPN IP the new config would assign to this machine — or null if there is none.
    /// Default-route AllowedIPs entries (0.0.0.0/0) are excluded from (A): a full tunnel isn't a
    /// routable "network" to compare against, and the remote network behind it is only knowable
    /// via (B) when the backend can supply it (Valenius-managed sidecar). Fail-open on either side
    /// being unreadable — this is a safety net, not a hard gate when detection itself is uncertain.
    /// </summary>
    public string? FindLocalLanConflict(string newConfigPath, IEnumerable<string>? statusRemoteLanCidrs)
    {
        var localCidrs = NetworkDetector.GetLocalLanCidrs();
        if (localCidrs.Length == 0) return null;

        var remoteCidrs = _configs.ReadAllowedIPs(newConfigPath)
            .Where(c => !IsDefaultRoute(c))
            .Concat(statusRemoteLanCidrs ?? [])
            .ToList();
        var assignedAddresses = _configs.ReadAddress(newConfigPath);

        foreach (var local in localCidrs)
        {
            foreach (var remote in remoteCidrs)
            {
                if (CidrsOverlap(local, remote))
                    return $"Your local network ({local}) overlaps with a network routed through this VPN " +
                           $"({remote}). Connecting would make local devices unreachable or misroute traffic " +
                           "meant for the remote network. Contact your administrator or change your local " +
                           "network before connecting.";
            }

            foreach (var addr in assignedAddresses)
            {
                if (CidrsOverlap(local, addr))
                    return $"Your local network ({local}) overlaps with the VPN address assigned to this " +
                           $"device ({addr}). Connecting would make this device unreachable on your local " +
                           "network. Contact your administrator before connecting.";
            }
        }

        return null;
    }

    private static bool IsDefaultRoute(string cidr) =>
        cidr.Trim() is "0.0.0.0/0" or "::/0";

    /// <summary>
    /// True if two IPv4 CIDRs overlap (one contains or equals the other). IPv6 entries and
    /// unparseable strings are treated as non-overlapping, except the catch-all routes which
    /// are handled by the caller. The default route overlaps everything.
    /// </summary>
    private static bool CidrsOverlap(string a, string b)
    {
        if (IsDefaultRoute(a) || IsDefaultRoute(b)) return true;
        if (!TryParseV4(a, out var addrA, out var prefA)) return false;
        if (!TryParseV4(b, out var addrB, out var prefB)) return false;

        var prefix = Math.Min(prefA, prefB);
        uint mask  = prefix == 0 ? 0u : ~((1u << (32 - prefix)) - 1u);
        return (addrA & mask) == (addrB & mask);
    }

    private static bool TryParseV4(string cidr, out uint addr, out int prefix)
    {
        addr = 0; prefix = 32;
        var s = cidr.Trim();
        var slash = s.IndexOf('/');
        var ipPart = slash < 0 ? s : s[..slash];
        if (slash >= 0 && !int.TryParse(s[(slash + 1)..], out prefix)) return false;
        if (prefix is < 0 or > 32) return false;
        if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        addr = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return true;
    }

    private async Task<(bool Success, string Output)> RunAsync(string arguments)
    {
        if (!IsWireGuardInstalled())
            return (false, $"WireGuard not found at {WireGuardExe}");

        var psi = new ProcessStartInfo
        {
            FileName = WireGuardExe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = (stdout + stderr).Trim();
            var success = process.ExitCode == 0;

            if (!success)
                _logger.LogWarning("wireguard.exe {Args} exited {Code}: {Output}", arguments, process.ExitCode, output);

            return (success, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run wireguard.exe {Args}", arguments);
            return (false, ex.Message);
        }
    }
}
