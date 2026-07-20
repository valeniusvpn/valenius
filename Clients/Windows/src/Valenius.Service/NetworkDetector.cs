using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Valenius.Service;

/// <summary>
/// Detects whether the machine is currently inside any of the configured trusted networks.
/// </summary>
internal static class NetworkDetector
{
    /// <summary>
    /// Returns true when the machine is considered "at home" — i.e. at least one
    /// trusted network entry matches the current network state.
    ///
    /// A network entry matches when:
    ///   1. Any active non-loopback/non-tunnel adapter has an IPv4 address in <c>Subnet</c>.
    ///   2. If <c>PrimaryDns</c> is configured, the same adapter's first IPv4 DNS also matches.
    /// </summary>
    public static bool IsInTrustedNetwork(TrustedNetworkEntry[] networks)
    {
        if (networks.Length == 0) return false;

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up
                     && a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

        foreach (var entry in networks)
        {
            if (!TryParseCidr(entry.Subnet, out var networkAddr, out int prefixLen))
                continue;

            foreach (var adapter in adapters)
            {
                var props = adapter.GetIPProperties();

                var localIp = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => u.Address)
                    .FirstOrDefault(ip => IsInSubnet(ip, networkAddr, prefixLen));

                if (localIp is null) continue;

                // Subnet matches — optionally check primary DNS
                if (entry.PrimaryDns is null)
                    return true;

                var primaryDns = props.DnsAddresses
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (primaryDns is not null && primaryDns.ToString() == entry.PrimaryDns)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the network CIDR (e.g. "192.168.1.0/24") of every up, non-loopback, non-tunnel
    /// IPv4 adapter address on this machine. Used by the pre-connect LAN-conflict check to
    /// determine this machine's own local LAN(s) before comparing against a profile's routes.
    /// </summary>
    public static string[] GetLocalLanCidrs()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up
                     && a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

        var cidrs = new List<string>();
        foreach (var adapter in adapters)
        {
            foreach (var u in adapter.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (u.PrefixLength is < 0 or > 32) continue;

                var addrBytes = u.Address.GetAddressBytes();
                var netBytes  = (byte[])addrBytes.Clone();
                var fullBytes = u.PrefixLength / 8;
                var remainingBits = u.PrefixLength % 8;
                for (var i = fullBytes; i < netBytes.Length; i++) netBytes[i] = 0;
                if (remainingBits > 0)
                {
                    var mask = (byte)(0xFF << (8 - remainingBits));
                    netBytes[fullBytes] = (byte)(addrBytes[fullBytes] & mask);
                }

                cidrs.Add($"{new IPAddress(netBytes)}/{u.PrefixLength}");
            }
        }
        return cidrs.ToArray();
    }

    // ── CIDR helpers ──────────────────────────────────────────────────────────

    private static bool TryParseCidr(string cidr, out IPAddress networkAddress, out int prefixLength)
    {
        networkAddress = IPAddress.None;
        prefixLength   = 0;

        var slash = cidr.IndexOf('/');
        if (slash < 0) return false;

        if (!IPAddress.TryParse(cidr[..slash], out networkAddress!)) return false;
        return int.TryParse(cidr[(slash + 1)..], out prefixLength)
            && prefixLength is >= 0 and <= 32;
    }

    private static bool IsInSubnet(IPAddress address, IPAddress networkAddress, int prefixLength)
    {
        var addrBytes = address.GetAddressBytes();
        var netBytes  = networkAddress.GetAddressBytes();
        if (addrBytes.Length != netBytes.Length) return false;

        var fullBytes     = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
            if (addrBytes[i] != netBytes[i]) return false;

        if (remainingBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((addrBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }

        return true;
    }
}
