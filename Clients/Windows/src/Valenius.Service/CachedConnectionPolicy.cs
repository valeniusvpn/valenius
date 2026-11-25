namespace Valenius.Service;

/// <summary>
/// Reusable policy required to reconnect after a backend outage or service restart.
/// Deliberately excludes API keys, enrollment secrets and one-shot backend commands.
/// Persisted with DPAPI because it includes internal network topology.
/// </summary>
internal sealed record CachedConnectionPolicy
{
    public int Version { get; init; } = 1;
    public string? ServerProfileName { get; init; }
    public string? ServerHealthUrl { get; init; }
    public string? ServerVpnIp { get; init; }
    public bool SkipConnectivityCheck { get; init; }
    public bool DeviceTunnelEnabled { get; init; }
    public GatewayProfileEntry[] GatewayProfiles { get; init; } = [];
    public FallbackEndpointEntry[] FallbackEndpoints { get; init; } = [];
    public LanCidrProfileEntry[] RemoteLanCidrsByProfile { get; init; } = [];
    public bool MfaRequired { get; init; }
    public string? MfaAuthorizeUrl { get; init; }
    public DateTime? MfaSessionExpiresAt { get; init; }
    public bool MfaEnrollmentOpen { get; init; }
    public string UpdateChannel { get; init; } = "stable";

    public static CachedConnectionPolicy FromStatus(ClientStatusResponse status) => new()
    {
        ServerProfileName = status.ServerProfileName,
        ServerHealthUrl = status.ServerHealthUrl,
        ServerVpnIp = status.ServerVpnIp,
        SkipConnectivityCheck = status.SkipConnectivityCheck,
        DeviceTunnelEnabled = status.DeviceTunnelEnabled,
        GatewayProfiles = status.GatewayProfiles ?? [],
        FallbackEndpoints = status.FallbackEndpoints ?? [],
        RemoteLanCidrsByProfile = status.RemoteLanCidrsByProfile ?? [],
        MfaRequired = status.MfaRequired,
        MfaAuthorizeUrl = status.MfaAuthorizeUrl,
        MfaSessionExpiresAt = status.MfaSessionExpiresAt,
        MfaEnrollmentOpen = status.MfaEnrollmentOpen,
        UpdateChannel = string.Equals(status.UpdateChannel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta" : "stable"
    };
}
