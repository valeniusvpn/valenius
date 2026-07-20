// Auto-connect policy + trusted-network detection — port of Clients/Linux/daemon/auto_connect.py
// (config) and NetworkDetector.IsInTrustedNetwork (Windows). Persisted to autoconnect.json.
//
// The admin value always overwrites `enabled` on each heartbeat; the user toggle
// (SetAutoConnect) is immediate but overwritten on the next heartbeat — mirrors Windows/Linux.

import Foundation
import SystemConfiguration

private let autoConnectPath = "\(appSupportDir)/autoconnect.json"

struct TrustedNetwork: Codable {
    var subnet: String
    var primaryDns: String?

    enum CodingKeys: String, CodingKey {
        case subnet = "Subnet"
        case primaryDns = "PrimaryDns"
    }
}

actor AutoConnectConfig {
    private var adminEnabled = false
    private var enabled = false
    private var profileName: String?
    private var trustedNetworks: [TrustedNetwork] = []

    private struct FileShape: Codable {
        var adminEnabled: Bool
        var enabled: Bool
        var profileName: String?
        var trustedNetworks: [TrustedNetwork]
        enum CodingKeys: String, CodingKey {
            case adminEnabled = "AdminEnabled"
            case enabled = "Enabled"
            case profileName = "ProfileName"
            case trustedNetworks = "TrustedNetworks"
        }
    }

    func load() {
        guard let data = FileManager.default.contents(atPath: autoConnectPath),
              let shape = try? JSONDecoder().decode(FileShape.self, from: data) else { return }
        adminEnabled = shape.adminEnabled
        enabled = shape.enabled
        profileName = shape.profileName
        trustedNetworks = shape.trustedNetworks
    }

    func updateFromBackend(adminEnabled: Bool, enabled: Bool, profileName: String?, trustedNetworks: [TrustedNetwork]) {
        self.adminEnabled = adminEnabled
        self.enabled = enabled
        self.profileName = profileName
        self.trustedNetworks = trustedNetworks
    }

    func setUserEnabled(_ value: Bool) { enabled = value }
    func isEnabled() -> Bool { enabled }
    func getProfileName() -> String? { profileName }
    func getTrustedNetworks() -> [TrustedNetwork] { trustedNetworks }

    func persist() {
        let shape = FileShape(adminEnabled: adminEnabled, enabled: enabled, profileName: profileName, trustedNetworks: trustedNetworks)
        guard let data = try? JSONEncoder().encode(shape) else { return }
        try? data.write(to: URL(fileURLWithPath: autoConnectPath))
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: autoConnectPath)
    }
}

enum TrustedNetworkDetector {
    /// True if a local IPv4 is within a trusted CIDR AND (if the entry sets one) the primary DNS
    /// matches. Byte-level CIDR match, mirrors Windows NetworkDetector.IsInTrustedNetwork.
    static func isInTrustedNetwork(_ networks: [TrustedNetwork]) -> Bool {
        guard !networks.isEmpty else { return false }
        let localIps = localIPv4Addresses()
        let primaryDns = primaryDnsServer()
        for entry in networks where !entry.subnet.isEmpty {
            guard let (net, prefix) = parseCidrV4(entry.subnet) else { continue }
            let mask: UInt32 = prefix == 0 ? 0 : (0xFFFF_FFFF << (32 - prefix))
            for ip in localIps {
                guard let ipInt = ipv4ToUInt32(ip) else { continue }
                if (ipInt & mask) == (net & mask) {
                    if entry.primaryDns == nil || entry.primaryDns == primaryDns { return true }
                }
            }
        }
        return false
    }

    /// Non-loopback, non-utun IPv4 addresses (we don't want a tunnel's own address to count).
    static func localIPv4Addresses() -> [String] {
        var results: [String] = []
        var addrs: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&addrs) == 0 else { return [] }
        defer { freeifaddrs(addrs) }
        var ptr = addrs
        while let cur = ptr {
            defer { ptr = cur.pointee.ifa_next }
            let name = String(cString: cur.pointee.ifa_name)
            if name == "lo0" || name.hasPrefix("utun") { continue }
            guard let sa = cur.pointee.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET) else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                results.append(String(cString: host))
            }
        }
        return results
    }

    /// Network CIDR (e.g. "192.168.1.0/24") for every non-loopback, non-utun IPv4 adapter
    /// address, keeping the netmask that `localIPv4Addresses()` discards. Used by the
    /// pre-connect LAN-conflict check to determine this machine's own local LAN(s) before
    /// comparing against a profile's routes. Mirrors Windows NetworkDetector.GetLocalLanCidrs /
    /// Linux config_manager.get_local_lan_cidrs.
    static func localLanCidrs() -> [String] {
        var results: [String] = []
        var addrs: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&addrs) == 0 else { return [] }
        defer { freeifaddrs(addrs) }
        var ptr = addrs
        while let cur = ptr {
            defer { ptr = cur.pointee.ifa_next }
            let name = String(cString: cur.pointee.ifa_name)
            if name == "lo0" || name.hasPrefix("utun") { continue }
            guard let sa = cur.pointee.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET) else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0,
                  let ipInt = ipv4ToUInt32(String(cString: host)) else { continue }
            guard let maskSa = cur.pointee.ifa_netmask, maskSa.pointee.sa_family == UInt8(AF_INET) else { continue }
            var maskHost = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(maskSa, socklen_t(maskSa.pointee.sa_len), &maskHost, socklen_t(maskHost.count), nil, 0, NI_NUMERICHOST) == 0,
                  let maskInt = ipv4ToUInt32(String(cString: maskHost)) else { continue }
            let prefix = maskInt.nonzeroBitCount
            let network = ipInt & maskInt
            results.append("\(uint32ToIPv4(network))/\(prefix)")
        }
        return results
    }

    /// First global nameserver (SCDynamicStore State:/Network/Global/DNS), mirroring Linux's
    /// /etc/resolv.conf read.
    static func primaryDnsServer() -> String? {
        guard let store = SCDynamicStoreCreate(nil, "com.stranto.valenius" as CFString, nil, nil),
              let dns = SCDynamicStoreCopyValue(store, "State:/Network/Global/DNS" as CFString) as? [String: Any],
              let servers = dns["ServerAddresses"] as? [String] else { return nil }
        return servers.first
    }
}

private func ipv4ToUInt32(_ s: String) -> UInt32? {
    let octets = s.split(separator: ".")
    guard octets.count == 4 else { return nil }
    var v: UInt32 = 0
    for o in octets {
        guard let n = UInt32(o), n <= 255 else { return nil }
        v = (v << 8) | n
    }
    return v
}

private func uint32ToIPv4(_ v: UInt32) -> String {
    "\((v >> 24) & 0xFF).\((v >> 16) & 0xFF).\((v >> 8) & 0xFF).\(v & 0xFF)"
}
