namespace Valenius.Service;

/// <summary>
/// Persisted to C:\ProgramData\Valenius\autoconnect.json.
/// AdminEnabled comes from the backend heartbeat.
/// Enabled is the effective user-visible toggle (admin can push it on; user can toggle either way).
/// </summary>
public class AutoConnectConfig
{
    /// <summary>Last value pushed by the backend for this client.</summary>
    public bool AdminEnabled { get; set; }

    /// <summary>
    /// Current effective state. Starts equal to AdminEnabled on first push;
    /// user can override freely via the tray toggle.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Profile to connect with; null = first available profile.</summary>
    public string? ProfileName { get; set; }

    public TrustedNetworkEntry[] TrustedNetworks { get; set; } = [];
}

public class TrustedNetworkEntry
{
    /// <summary>CIDR notation, e.g. "10.100.1.0/24".</summary>
    public string Subnet { get; set; } = "";

    /// <summary>Optional primary DNS to match, e.g. "10.100.1.10".</summary>
    public string? PrimaryDns { get; set; }
}
