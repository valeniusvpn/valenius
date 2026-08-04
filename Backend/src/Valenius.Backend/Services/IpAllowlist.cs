using System.Net;

namespace Valenius.Backend.Services;

/// <summary>Matches a request's source IP against an <see cref="Models.ApiToken.IpAllowlist"/>
/// value — a comma-separated list of CIDR ranges and/or bare IPs. See
/// docs/design/management-api.md §4.4 step 5.</summary>
public static class IpAllowlist
{
    /// <summary>True if <paramref name="allowlist"/> is null/empty (any source IP allowed —
    /// the common case) or <paramref name="remoteIp"/> matches one of its entries.</summary>
    public static bool IsAllowed(string? allowlist, IPAddress? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(allowlist))
            return true;
        if (remoteIp is null)
            return false;

        foreach (var entry in allowlist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(entry, out var network) && network.Contains(remoteIp))
                return true;
            if (IPAddress.TryParse(entry, out var single) && single.Equals(remoteIp))
                return true;
        }

        return false;
    }
}
