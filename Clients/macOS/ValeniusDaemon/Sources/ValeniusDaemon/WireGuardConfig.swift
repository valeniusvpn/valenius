// Parse a WireGuard `.conf` into the structured form the TunnelEngine needs, and serialize
// it to the wireguard-go UAPI `set=1` wire format. There is no `wg`/`wg-quick` binary in the
// loop on macOS — we speak UAPI directly, so this file owns the .conf → UAPI translation.
//
// Keys in a `.conf` are base64 (44 chars); UAPI wants lowercase hex (64 chars). Endpoints
// are resolved to ip:port by wireguard-go itself, so we pass them through verbatim.

import Foundation

struct WireGuardConfig {
    struct Peer {
        var publicKey: String          // base64
        var presharedKey: String?      // base64
        var endpoint: String?
        var allowedIps: [String] = []
        var persistentKeepalive: Int?
    }

    var privateKey: String             // base64
    var addresses: [String] = []       // Interface Address = (CIDRs assigned to utun)
    var dns: [String] = []             // plain servers + search domains, in listed order
    var mtu: Int?
    var listenPort: Int?
    var peers: [Peer] = []

    enum ParseError: Error, CustomStringConvertible {
        case noPrivateKey
        case noPeers
        case badKey(String)
        var description: String {
            switch self {
            case .noPrivateKey: return "config has no [Interface] PrivateKey"
            case .noPeers: return "config has no [Peer] section"
            case .badKey(let k): return "not a valid base64 WireGuard key: \(k)"
            }
        }
    }

    /// Parse assuming the content already passed `ConfigValidation.validateContent` (allowlist).
    static func parse(_ content: String) throws -> WireGuardConfig {
        var privateKey: String?
        var addresses: [String] = []
        var dns: [String] = []
        var mtu: Int?
        var listenPort: Int?
        var peers: [Peer] = []
        var current: Peer?
        var section = ""

        func flushPeer() {
            if let c = current { peers.append(c) }
            current = nil
        }

        for rawLine in content.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            if line.hasPrefix("[") {
                let s = line.lowercased()
                if s == "[peer]" { flushPeer(); current = Peer(publicKey: "") }
                section = s
                continue
            }
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)

            if section == "[interface]" {
                switch key {
                case "privatekey": privateKey = value
                case "address": addresses += value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                case "dns": dns += value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                case "mtu": mtu = Int(value)
                case "listenport": listenPort = Int(value)
                default: break
                }
            } else if section == "[peer]" {
                switch key {
                case "publickey": current?.publicKey = value
                case "presharedkey": current?.presharedKey = value
                case "endpoint": current?.endpoint = value
                case "allowedips": current?.allowedIps += value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                case "persistentkeepalive": current?.persistentKeepalive = Int(value)
                default: break
                }
            }
        }
        flushPeer()

        guard let pk = privateKey else { throw ParseError.noPrivateKey }
        guard !peers.isEmpty else { throw ParseError.noPeers }
        return WireGuardConfig(privateKey: pk, addresses: addresses, dns: dns, mtu: mtu, listenPort: listenPort, peers: peers)
    }

    /// DNS entries that are IP addresses (go on the resolver's `nameserver` list). Anything
    /// non-IP is a search domain (see `searchDomains`) — mirrors WireGuard's DNS= handling.
    var nameservers: [String] { dns.filter { isIpAddress($0) } }
    var searchDomains: [String] { dns.filter { !isIpAddress($0) } }

    // MARK: UAPI serialization

    /// Build the `set=1` UAPI request that configures wireguard-go. `replacePeers=true` makes
    /// this a full (re)configuration. Terminated by a blank line, per the UAPI protocol.
    func uapiSetRequest() throws -> String {
        var lines = ["set=1"]
        lines.append("private_key=\(try base64ToHex(privateKey))")
        if let listenPort { lines.append("listen_port=\(listenPort)") }
        lines.append("replace_peers=true")
        for peer in peers {
            lines.append("public_key=\(try base64ToHex(peer.publicKey))")
            if let psk = peer.presharedKey, !psk.isEmpty {
                lines.append("preshared_key=\(try base64ToHex(psk))")
            }
            if let ep = peer.endpoint { lines.append("endpoint=\(ep)") }
            if let ka = peer.persistentKeepalive { lines.append("persistent_keepalive_interval=\(ka)") }
            lines.append("replace_allowed_ips=true")
            for aip in peer.allowedIps { lines.append("allowed_ip=\(aip)") }
        }
        lines.append("") // blank line terminator
        lines.append("")
        return lines.joined(separator: "\n")
    }
}

private func base64ToHex(_ b64: String) throws -> String {
    guard let data = Data(base64Encoded: b64), data.count == 32 else {
        throw WireGuardConfig.ParseError.badKey(b64)
    }
    return data.map { String(format: "%02x", $0) }.joined()
}

private func isIpAddress(_ s: String) -> Bool {
    var v4 = in_addr()
    if s.withCString({ inet_pton(AF_INET, $0, &v4) }) == 1 { return true }
    var v6 = in6_addr()
    if s.withCString({ inet_pton(AF_INET6, $0, &v6) }) == 1 { return true }
    return false
}
