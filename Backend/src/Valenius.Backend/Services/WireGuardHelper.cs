using System.Net;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Valenius.Backend.Services;

/// <summary>
/// Helpers for WireGuard key-pair generation, IP allocation, and client config building.
/// Key generation uses Bouncy Castle's X25519 implementation (cross-platform; works on Linux).
/// </summary>
public static class WireGuardHelper
{
    /// <summary>
    /// Generates a WireGuard X25519 key pair.
    /// Returns (PrivateKeyBase64, PublicKeyBase64) — same 32-byte raw format wg(8) uses.
    /// </summary>
    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var gen = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();

        var priv = new byte[X25519PrivateKeyParameters.KeySize];
        var pub  = new byte[X25519PublicKeyParameters.KeySize];
        ((X25519PrivateKeyParameters)pair.Private).Encode(priv, 0);
        ((X25519PublicKeyParameters) pair.Public ).Encode(pub,  0);

        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
    }

    /// <summary>
    /// Finds the first unallocated host IP in <paramref name="cidr"/>.
    /// .1 is always reserved for the server; allocation starts at .2.
    /// Returns null if the CIDR is invalid or the subnet is full.
    /// </summary>
    public static string? AllocateIp(string cidr, IEnumerable<string> usedIps)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0) return null;
        if (!IPAddress.TryParse(cidr[..slash], out var baseIp)) return null;
        if (!int.TryParse(cidr[(slash + 1)..], out var prefix) || prefix < 8 || prefix > 30) return null;

        var b        = baseIp.GetAddressBytes();
        var network  = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var maskBits = prefix == 0 ? 0u : ~((1u << (32 - prefix)) - 1u);
        var net      = network & maskBits;
        var broadcast= net | ~maskBits;
        var used     = new HashSet<string>(usedIps);

        for (var ip = net + 2; ip < broadcast; ip++)
        {
            var s = new IPAddress(new[] {
                (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip
            }).ToString();
            if (!used.Contains(s)) return s;
        }
        return null;
    }

    /// <summary>
    /// Builds a WireGuard client .conf file content ready to push via PendingConfig.
    /// <paramref name="allowedIPs"/> should be the already-resolved effective value
    /// (client override → customer default → "0.0.0.0/0, ::/0").
    /// <paramref name="dnsSearchDomains"/> (optional, comma-separated) are appended to the
    /// <c>DNS =</c> line. WireGuard for Windows turns the non-IP entries into NRPT split-DNS
    /// rules, so queries for those domains resolve through this tunnel's DNS while everything
    /// else stays on the system default — the mechanism that lets multiple tunnels each own
    /// their own DNS without colliding. Ignored when no DNS server is set (NRPT needs one).
    /// </summary>
    public static string BuildClientConfig(
        string clientPrivateKey,
        string assignedIp,
        string serverPublicKey,
        string serverEndpoint,
        string? dnsServer,
        string allowedIPs,
        string? dnsSearchDomains = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {clientPrivateKey}");
        sb.AppendLine($"Address = {assignedIp}/32");
        if (!string.IsNullOrWhiteSpace(dnsServer))
            sb.AppendLine($"DNS = {BuildDnsLine(dnsServer, dnsSearchDomains)}");
        sb.AppendLine();
        sb.AppendLine("[Peer]");
        sb.AppendLine($"PublicKey = {serverPublicKey}");
        sb.AppendLine($"Endpoint = {serverEndpoint}");
        sb.AppendLine($"AllowedIPs = {allowedIPs}");
        sb.AppendLine("PersistentKeepalive = 25");
        return sb.ToString();
    }

    /// <summary>
    /// Composes the <c>DNS =</c> value from one or more server IPs and optional search domains.
    /// Both fields may be comma-separated; entries are trimmed and re-joined as
    /// "ip1, ip2, domain1, domain2" (servers first, then domains — the order WireGuard expects).
    /// </summary>
    public static string BuildDnsLine(string dnsServer, string? dnsSearchDomains)
    {
        var parts = dnsServer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!string.IsNullOrWhiteSpace(dnsSearchDomains))
            parts.AddRange(dnsSearchDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Builds the AllowedIPs value for a FOREIGN sidecar profile. Must never fall back to the
    /// source customer's own AllowedIPs/full-tunnel default — that's the native default for that
    /// customer's OWN users, and a foreign profile is borrowed access to someone else's network,
    /// not the client's own primary VPN. A full-tunnel foreign profile would route ALL of the
    /// client's traffic through a network it doesn't own just to reach one customer's resources.
    /// Scoped to exactly what's needed to reach this sidecar: its own transit subnet (which also
    /// covers the gateway /health probe target — no separate gateway-/32 injection needed) plus
    /// its physical LAN CIDR(s) (<see cref="Customer.SidecarLanCidrs"/>, self-reported via /info),
    /// if known.
    /// </summary>
    public static string ForeignAllowedIPs(string? transitSubnetCidr, string? sidecarLanCidrs)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(transitSubnetCidr)) parts.Add(transitSubnetCidr);
        if (!string.IsNullOrEmpty(sidecarLanCidrs))
            parts.AddRange(sidecarLanCidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return parts.Count > 0 ? string.Join(", ", parts.Distinct()) : (transitSubnetCidr ?? "");
    }

    /// <summary>
    /// Ensures the VPN gateway IP is reachable through the tunnel for split-tunnel configs.
    /// When AllowedIPs does NOT cover all IPv4 traffic (i.e. does not contain 0.0.0.0/0),
    /// prepends "gatewayIp/32" so the Phase-2 connectivity probe can reach the server
    /// through the tunnel.  Full-tunnel configs and null/empty gateway are returned as-is.
    /// </summary>
    public static string EnsureGatewayInAllowedIPs(string allowedIPs, string? gatewayIp)
    {
        if (string.IsNullOrEmpty(gatewayIp)) return allowedIPs;
        if (allowedIPs.Contains("0.0.0.0/0")) return allowedIPs;
        var entry = gatewayIp.Trim() + "/32";
        if (allowedIPs.Split(',').Any(p => p.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase)))
            return allowedIPs;
        return entry + ", " + allowedIPs;
    }

    /// <summary>
    /// Normalises a sidecar interface address (e.g. "10.8.0.1/24") to its network CIDR
    /// ("10.8.0.0/24"). Returns null for null/empty/unparseable or non-IPv4 input. A bare IP
    /// with no prefix is treated as /32.
    /// </summary>
    public static string? NetworkCidr(string? interfaceAddress)
    {
        if (string.IsNullOrWhiteSpace(interfaceAddress)) return null;
        var s = interfaceAddress.Trim();
        var slash = s.IndexOf('/');
        var ipPart = slash < 0 ? s : s[..slash];
        var prefix = 32;
        if (slash >= 0 && (!int.TryParse(s[(slash + 1)..], out prefix) || prefix is < 0 or > 32))
            return null;
        if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        var b    = ip.GetAddressBytes();
        var addr = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var mask = prefix == 0 ? 0u : ~((1u << (32 - prefix)) - 1u);
        var net  = addr & mask;
        var netIp = new IPAddress(new[] { (byte)(net >> 24), (byte)(net >> 16), (byte)(net >> 8), (byte)net });
        return $"{netIp}/{prefix}";
    }

    /// <summary>
    /// True if two IPv4 CIDRs overlap (either contains or equals the other). Unparseable or
    /// non-IPv4 inputs are treated as non-overlapping. Used to keep sidecar transit networks
    /// globally disjoint.
    /// </summary>
    public static bool CidrsOverlap(string? a, string? b)
    {
        if (!TryParseV4Cidr(a, out var addrA, out var prefA)) return false;
        if (!TryParseV4Cidr(b, out var addrB, out var prefB)) return false;
        var prefix = Math.Min(prefA, prefB);
        var mask   = prefix == 0 ? 0u : ~((1u << (32 - prefix)) - 1u);
        return (addrA & mask) == (addrB & mask);
    }

    /// <summary>
    /// True when <paramref name="subnet"/> is set and disjoint from every CIDR in
    /// <paramref name="otherSubnets"/> (which must exclude the subject's own row). This is the
    /// gate for the sidecar gateway `/health` check: only transit-unique sidecars get the
    /// gateway `/32` in their configs and a gateway-probe entry in the heartbeat.
    /// </summary>
    public static bool IsTransitUnique(string? subnet, IEnumerable<string?> otherSubnets)
    {
        if (string.IsNullOrWhiteSpace(subnet)) return false;
        return !otherSubnets.Any(o => CidrsOverlap(subnet, o));
    }

    private static bool TryParseV4Cidr(string? cidr, out uint addr, out int prefix)
    {
        addr = 0; prefix = 32;
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var s = cidr.Trim();
        var slash = s.IndexOf('/');
        var ipPart = slash < 0 ? s : s[..slash];
        if (slash >= 0 && !int.TryParse(s[(slash + 1)..], out prefix)) return false;
        if (prefix is < 0 or > 32) return false;
        if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var by = ip.GetAddressBytes();
        addr = (uint)(by[0] << 24 | by[1] << 16 | by[2] << 8 | by[3]);
        return true;
    }

    /// <summary>
    /// Returns the WireGuard tunnel profile name for a customer.
    /// When <paramref name="vpnPrefix"/> is set (letters only), the result is "{prefix}-VPN".
    /// Otherwise a sanitised version of <paramref name="customerName"/> is used.
    /// </summary>
    public static string ProfileName(string? vpnPrefix, string customerName)
    {
        if (!string.IsNullOrWhiteSpace(vpnPrefix))
            return vpnPrefix.Trim() + "-VPN";

        var clean = new StringBuilder();
        foreach (var c in customerName)
            clean.Append(char.IsLetterOrDigit(c) ? c : '_');
        var result = clean.ToString().Trim('_');
        if (result.Length > 44) result = result[..44];
        return result + "_VPN";
    }
}
