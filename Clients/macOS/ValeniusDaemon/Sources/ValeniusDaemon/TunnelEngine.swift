// Native WireGuard tunnel engine — the macOS replacement for `wg-quick`. Drives the bundled
// wireguard-go via UAPI, and does address/route/DNS setup itself (ifconfig/route/scutil).
// See docs/macos-client-concept.md → "Why not wg-quick".
//
// Lifecycle per tunnel:
//   1. spawn `wireguard-go utun` (WG_PROCESS_FOREGROUND=1 so we own the pid; WG_TUN_NAME_FILE
//      reports the kernel-assigned utunN reliably)
//   2. push private key + peers over the UAPI socket (in-memory config; nothing on disk)
//   3. ifconfig the addresses, install routes (0.0.0.0/0 → split + endpoint host route),
//      set scoped DNS + search domains via scutil
//   4. teardown = terminate wireguard-go (the utun disappears with the process) + remove the
//      scutil DNS entry. No config file is ever needed for teardown.
//
// A run/<profile>.json marker (pid + iface) lets startup reconciliation re-adopt tunnels
// that outlived a daemon restart/upgrade.

import Darwin
import Foundation

private let runDir = "\(appSupportDir)/run"
/// Bundled by the pkg (M7). Falls back to a dev build on PATH / /tmp for local iteration.
private let bundledWireguardGo = "\(appSupportDir)/bin/wireguard-go"

struct TunnelHandle {
    let profile: String
    let iface: String
    let pid: Int32
    let endpointHost: String?  // for full-tunnel host-route cleanup
}

struct TunnelMarker: Codable {
    var profile: String
    var iface: String
    var pid: Int32
    var endpointHost: String?
}

enum TunnelEngineError: Error, CustomStringConvertible {
    case wireguardGoMissing
    case spawnFailed(String)
    case ifaceNameTimeout
    case uapiTimeout(String)

    var description: String {
        switch self {
        case .wireguardGoMissing: return "wireguard-go binary not found (expected at \(bundledWireguardGo))"
        case .spawnFailed(let m): return "failed to spawn wireguard-go: \(m)"
        case .ifaceNameTimeout: return "wireguard-go did not report its utun interface in time"
        case .uapiTimeout(let i): return "wireguard-go UAPI socket for \(i) never appeared"
        }
    }
}

/// Not an actor: it holds live `Process` handles and is only ever called from within the
/// `DaemonCore` actor, which already serializes access. Keeps Process/pid handling simple.
final class TunnelEngine {
    private var processes: [String: Process] = [:]  // profile -> wireguard-go process

    private func wireguardGoPath() -> String? {
        if FileManager.default.isExecutableFile(atPath: bundledWireguardGo) { return bundledWireguardGo }
        // Dev fallbacks (never used in a shipped pkg — the binary is always bundled there).
        for dev in ["/tmp/wireguard-go-bin", "/opt/homebrew/bin/wireguard-go", "/usr/local/bin/wireguard-go"] {
            if FileManager.default.isExecutableFile(atPath: dev) { return dev }
        }
        return nil
    }

    // MARK: Bring-up

    /// Bring `profile` up from already-validated plaintext config. Returns the handle, or
    /// throws (leaving nothing running — best-effort cleanup on failure).
    func up(profile: String, config content: String) throws -> TunnelHandle {
        let cfg = try WireGuardConfig.parse(content)
        guard let wgPath = wireguardGoPath() else { throw TunnelEngineError.wireguardGoMissing }

        try? FileManager.default.createDirectory(atPath: wireguardSocketDir, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        try? FileManager.default.createDirectory(atPath: runDir, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])

        // wireguard-go writes the kernel-assigned utunN here.
        let nameFile = "\(runDir)/.\(profile).name"
        try? FileManager.default.removeItem(atPath: nameFile)

        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: wgPath)
        proc.arguments = ["utun"]
        var env = ProcessInfo.processInfo.environment
        env["WG_PROCESS_FOREGROUND"] = "1"
        env["WG_TUN_NAME_FILE"] = nameFile
        env["LOG_LEVEL"] = "error"
        proc.environment = env

        do {
            try proc.run()
        } catch {
            throw TunnelEngineError.spawnFailed("\(error)")
        }

        // Read the assigned interface name (wireguard-go writes it once the utun is created).
        guard let iface = waitForFile(nameFile, timeout: 5)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !iface.isEmpty else {
            proc.terminate()
            throw TunnelEngineError.ifaceNameTimeout
        }
        try? FileManager.default.removeItem(atPath: nameFile)

        // Wait for the UAPI socket, then push the config.
        guard waitForPath(UapiClient.socketPath(iface: iface), timeout: 5) else {
            proc.terminate()
            throw TunnelEngineError.uapiTimeout(iface)
        }
        do {
            try UapiClient.applyConfig(iface: iface, setRequest: try cfg.uapiSetRequest())
            try configureNetwork(iface: iface, cfg: cfg)
        } catch {
            proc.terminate()
            teardownNetwork(iface: iface, searchDomains: cfg.searchDomains)
            throw error
        }

        processes[profile] = proc
        let endpointHost = cfg.peers.first?.endpoint.flatMap { $0.split(separator: ":").first.map(String.init) }
        let handle = TunnelHandle(profile: profile, iface: iface, pid: proc.processIdentifier, endpointHost: endpointHost)
        writeMarker(handle, searchDomains: cfg.searchDomains)
        log("Tunnel '\(profile)' up on \(iface) (pid \(proc.processIdentifier))")
        return handle
    }

    // MARK: Teardown

    func down(profile: String, iface: String?, searchDomains: [String] = []) {
        if let iface { teardownNetwork(iface: iface, searchDomains: searchDomains) }
        if let proc = processes.removeValue(forKey: profile) {
            proc.terminate()
        } else if let marker = readMarker(profile: profile), processExists(marker.pid) {
            // Adopted-across-restart tunnel: we don't hold the Process, kill by pid.
            kill(marker.pid, SIGTERM)
        }
        removeMarker(profile: profile)
        log("Tunnel '\(profile)' down")
    }

    /// The utun interface currently backing a profile (from the run marker), for handshake reads.
    func iface(for profile: String) -> String? {
        readMarker(profile: profile)?.iface
    }

    // MARK: Startup reconciliation

    /// Read run/*.json markers, keep those whose wireguard-go pid + utun still exist, clean up
    /// dead ones. Returns the live (profile, iface) pairs for DaemonState to adopt as 'Unknown'
    /// (disconnectable by anyone) — mirrors Linux reconcile_active_tunnels().
    func reconcile() -> [(profile: String, iface: String)] {
        guard let files = try? FileManager.default.contentsOfDirectory(atPath: runDir) else { return [] }
        var live: [(String, String)] = []
        for file in files where file.hasSuffix(".json") {
            guard let marker = readMarkerFile("\(runDir)/\(file)") else {
                try? FileManager.default.removeItem(atPath: "\(runDir)/\(file)")
                continue
            }
            if processExists(marker.pid) && interfaceExists(marker.iface) {
                live.append((marker.profile, marker.iface))
                log("Adopted already-active tunnel '\(marker.profile)' on \(marker.iface) (pid \(marker.pid))")
            } else {
                try? FileManager.default.removeItem(atPath: "\(runDir)/\(file)")
            }
        }
        return live
    }

    // MARK: Network configuration (ifconfig / route / scutil)

    private func configureNetwork(iface: String, cfg: WireGuardConfig) throws {
        for addr in cfg.addresses {
            let isV6 = addr.contains(":")
            let ipOnly = addr.split(separator: "/").first.map(String.init) ?? addr
            if isV6 {
                run("/sbin/ifconfig", [iface, "inet6", addr, "alias"])
            } else {
                // macOS utun wants an explicit peer address; a /32 to itself is the wg-quick idiom.
                run("/sbin/ifconfig", [iface, "inet", addr, ipOnly, "alias"])
            }
        }
        if let mtu = cfg.mtu {
            run("/sbin/ifconfig", [iface, "mtu", "\(mtu)"])
        }
        run("/sbin/ifconfig", [iface, "up"])
        installRoutes(iface: iface, cfg: cfg)
        setDns(iface: iface, servers: cfg.nameservers, searchDomains: cfg.searchDomains)
    }

    private func installRoutes(iface: String, cfg: WireGuardConfig) {
        for peer in cfg.peers {
            for aip in peer.allowedIps {
                if aip == "0.0.0.0/0" {
                    // Full-tunnel: split default into two /1 routes so an explicit host route to
                    // the endpoint (added below) still wins over the tunnel. Mirrors wg-quick.
                    run("/sbin/route", ["-q", "-n", "add", "-inet", "0.0.0.0/1", "-interface", iface])
                    run("/sbin/route", ["-q", "-n", "add", "-inet", "128.0.0.0/1", "-interface", iface])
                    if let host = peer.endpoint?.split(separator: ":").first.map(String.init), isIpv4Literal(host) {
                        // Pin the endpoint to the pre-existing default route so handshakes escape the tunnel.
                        run("/sbin/route", ["-q", "-n", "add", "-inet", "\(host)/32", "-gateway", defaultGatewayV4() ?? ""])
                    }
                } else if aip == "::/0" {
                    run("/sbin/route", ["-q", "-n", "add", "-inet6", "::/1", "-interface", iface])
                    run("/sbin/route", ["-q", "-n", "add", "-inet6", "8000::/1", "-interface", iface])
                } else {
                    let family = aip.contains(":") ? "-inet6" : "-inet"
                    run("/sbin/route", ["-q", "-n", "add", family, aip, "-interface", iface])
                }
            }
        }
    }

    /// Scoped DNS via scutil: create State:/Network/Service/valenius-<iface>/DNS with the
    /// tunnel's nameservers + search domains. macOS's scoped-resolver mechanism is the
    /// split-DNS analogue of Windows NRPT (concept doc flags this as needing M4 validation).
    private func setDns(iface: String, servers: [String], searchDomains: [String]) {
        guard !servers.isEmpty else { return }
        let key = "State:/Network/Service/valenius-\(iface)/DNS"
        var cmds = "d.init\n"
        cmds += "d.add ServerAddresses * " + servers.joined(separator: " ") + "\n"
        if !searchDomains.isEmpty {
            cmds += "d.add SearchDomains * " + searchDomains.joined(separator: " ") + "\n"
            cmds += "d.add SupplementalMatchDomains * " + searchDomains.joined(separator: " ") + "\n"
        }
        cmds += "set \(key)\n"
        runWithStdin("/usr/sbin/scutil", stdin: cmds)
    }

    private func teardownNetwork(iface: String, searchDomains: [String]) {
        let key = "State:/Network/Service/valenius-\(iface)/DNS"
        runWithStdin("/usr/sbin/scutil", stdin: "remove \(key)\n")
        // Routes and the utun's addresses disappear with the interface when wireguard-go exits;
        // no explicit route flush needed.
    }

    // MARK: Markers

    private func writeMarker(_ handle: TunnelHandle, searchDomains: [String]) {
        let marker = TunnelMarker(profile: handle.profile, iface: handle.iface, pid: handle.pid, endpointHost: handle.endpointHost)
        guard let data = try? JSONEncoder().encode(marker) else { return }
        let path = "\(runDir)/\(handle.profile).json"
        try? data.write(to: URL(fileURLWithPath: path))
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
    }

    private func readMarker(profile: String) -> TunnelMarker? {
        readMarkerFile("\(runDir)/\(profile).json")
    }

    private func readMarkerFile(_ path: String) -> TunnelMarker? {
        guard let data = FileManager.default.contents(atPath: path) else { return nil }
        return try? JSONDecoder().decode(TunnelMarker.self, from: data)
    }

    private func removeMarker(profile: String) {
        try? FileManager.default.removeItem(atPath: "\(runDir)/\(profile).json")
    }
}

// MARK: - Process / interface helpers

private func processExists(_ pid: Int32) -> Bool {
    kill(pid, 0) == 0 || errno == EPERM
}

private func interfaceExists(_ iface: String) -> Bool {
    var addrs: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&addrs) == 0 else { return false }
    defer { freeifaddrs(addrs) }
    var ptr = addrs
    while let cur = ptr {
        if String(cString: cur.pointee.ifa_name) == iface { return true }
        ptr = cur.pointee.ifa_next
    }
    return false
}

private func isIpv4Literal(_ s: String) -> Bool {
    var v4 = in_addr()
    return s.withCString { inet_pton(AF_INET, $0, &v4) } == 1
}

/// The current default-route IPv4 gateway (for pinning a full-tunnel endpoint host route).
private func defaultGatewayV4() -> String? {
    let out = runCapturing("/sbin/route", ["-n", "get", "default"])
    for line in out.split(separator: "\n") {
        let t = line.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("gateway:") {
            return t.dropFirst("gateway:".count).trimmingCharacters(in: .whitespaces)
        }
    }
    return nil
}

private func waitForFile(_ path: String, timeout: TimeInterval) -> String? {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if let data = FileManager.default.contents(atPath: path), !data.isEmpty {
            return String(decoding: data, as: UTF8.self)
        }
        usleep(50_000)
    }
    return nil
}

private func waitForPath(_ path: String, timeout: TimeInterval) -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if FileManager.default.fileExists(atPath: path) { return true }
        usleep(50_000)
    }
    return false
}

@discardableResult
private func run(_ path: String, _ args: [String]) -> Int32 {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    do { try p.run(); p.waitUntilExit(); return p.terminationStatus }
    catch { logError("exec \(path) \(args.joined(separator: " ")) failed: \(error)"); return -1 }
}

private func runCapturing(_ path: String, _ args: [String]) -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = FileHandle.nullDevice
    do {
        try p.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
    } catch { return "" }
}

private func runWithStdin(_ path: String, stdin: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    let pipe = Pipe()
    p.standardInput = pipe
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    do {
        try p.run()
        pipe.fileHandleForWriting.write(Data(stdin.utf8))
        pipe.fileHandleForWriting.closeFile()
        p.waitUntilExit()
    } catch { logError("exec \(path) (stdin) failed: \(error)") }
}
