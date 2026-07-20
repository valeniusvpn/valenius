// WireGuard config storage — port of Clients/Linux/daemon/config_manager.py.
//
// macOS deviation (see docs/macos-client-concept.md → "Why not wg-quick"): there is NO
// temp dir. Plaintext configs never touch disk — decrypt happens in memory and the result
// goes straight to the wireguard-go UAPI (TunnelEngine). So there is no prepare/cleanup
// temp-file dance and no `wg-quick down` re-decrypt; teardown needs no config file at all.
//
// Encryption is CryptoKit AES-256-GCM with a machine-local 32-byte key, wire-compatible
// with the Linux/Windows on-disk layout: [12-byte nonce][ciphertext][16-byte tag] — which
// is exactly CryptoKit's `SealedBox.combined`.

import CryptoKit
import Foundation
import ValeniusShared

private let usersDir = "\(appSupportDir)/users"
private let stagedDir = "\(appSupportDir)/staged"
private let masterKeyPath = "\(appSupportDir)/master.key"

private let encExt = ".conf.enc"
private let plainExt = ".conf"

/// Only these [Interface]/[Peer] settings are permitted in a config the daemon will bring
/// up. Even though macOS drives the tunnel natively (no wg-quick shell hooks), we keep the
/// SAME allowlist as Linux: it's the local-user → root guard at the IPC boundary (the
/// socket is world-writable; LOCAL_PEERCRED authenticates the uid but any local user may
/// connect), and it rejects `PostUp`/`PostDown`/`SaveConfig`/`Table` by construction.
private let allowedConfigKeys: Set<String> = [
    "privatekey", "address", "dns", "mtu", "listenport", "fwmark",              // [Interface]
    "publickey", "presharedkey", "endpoint", "allowedips", "persistentkeepalive", // [Peer]
]
private let allowedConfigSections: Set<String> = ["[interface]", "[peer]"]

/// Pure, stateless validation + name helpers (no disk state) — safe to call anywhere.
enum ConfigValidation {
    /// Returns nil if `content` is a safe WireGuard config, else an error string. Applied
    /// at the UploadConfig boundary AND again before every tunnel-up (defense in depth), so
    /// admin-pushed / staged / foreign configs are filtered too. Never loosen; never move
    /// downstream of decryption.
    static func validateContent(_ content: String?) -> String? {
        guard let content, !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return "Config content is empty."
        }
        for raw in content.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            if line.hasPrefix("[") {
                if !allowedConfigSections.contains(line.lowercased()) {
                    return "Unsupported config section: \(line)"
                }
                continue
            }
            guard let eq = line.firstIndex(of: "=") else {
                return "Malformed config line: \(line)"
            }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            if !allowedConfigKeys.contains(key) {
                return "Config directive not allowed: \(line[..<eq].trimmingCharacters(in: .whitespaces))"
            }
        }
        return nil
    }

    static func sanitizeProfileName(_ filename: String) -> String {
        let stem = (filename as NSString).deletingPathExtension
        let safe = String(stem.map { $0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" ? $0 : "_" })
        return String((safe.isEmpty ? "profile" : safe).prefix(50))
    }
}

/// Encrypted config storage + staging. Actor-isolated for the same reason Linux guards the
/// key cache — concurrent callers from the IPC threads and the heartbeat/poll tasks.
actor ConfigManager {
    private var cachedKey: SymmetricKey?
    private let fm = FileManager.default

    // MARK: Startup

    /// Create the data dirs (0700) and encrypt any legacy plain .conf files. Mirrors
    /// ensure_dirs() + migrate_plain_configs(). (No temp dir to evict — macOS has none.)
    func bootstrap() {
        for dir in [appSupportDir, usersDir, stagedDir] {
            try? fm.createDirectory(atPath: dir, withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
        }
        migratePlainConfigs()
    }

    private func migratePlainConfigs() {
        guard let userDirs = try? fm.contentsOfDirectory(atPath: usersDir) else { return }
        for user in userDirs {
            let dir = "\(usersDir)/\(user)"
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: dir, isDirectory: &isDir), isDir.boolValue else { continue }
            guard let files = try? fm.contentsOfDirectory(atPath: dir) else { continue }
            for file in files where file.hasSuffix(plainExt) && !file.hasSuffix(encExt) {
                let name = profileName(fromFile: file)
                guard let name else { continue }
                let plainPath = "\(dir)/\(file)"
                let encPath = "\(dir)/\(name)\(encExt)"
                if fm.fileExists(atPath: encPath) {
                    try? fm.removeItem(atPath: plainPath)
                    continue
                }
                if let data = fm.contents(atPath: plainPath), let enc = try? encrypt(data) {
                    try? enc.write(to: URL(fileURLWithPath: encPath))
                    try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: encPath)
                    try? fm.removeItem(atPath: plainPath)
                }
            }
        }
    }

    // MARK: Encryption (CryptoKit AES-256-GCM, machine-local key)

    private func key() throws -> SymmetricKey {
        if let cachedKey { return cachedKey }
        let k: SymmetricKey
        if let data = fm.contents(atPath: masterKeyPath), data.count == 32 {
            k = SymmetricKey(data: data)
        } else {
            k = SymmetricKey(size: .bits256)
            let raw = k.withUnsafeBytes { Data($0) }
            try? fm.createDirectory(atPath: appSupportDir, withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
            try raw.write(to: URL(fileURLWithPath: masterKeyPath))
            try fm.setAttributes([.posixPermissions: 0o400], ofItemAtPath: masterKeyPath)
        }
        cachedKey = k
        return k
    }

    private func encrypt(_ data: Data) throws -> Data {
        let box = try AES.GCM.seal(data, using: try key())
        // `combined` = nonce(12) || ciphertext || tag(16) — matches the Linux/Windows layout.
        guard let combined = box.combined else { throw CocoaError(.coderInvalidValue) }
        return combined
    }

    private func decrypt(_ data: Data) throws -> Data {
        let box = try AES.GCM.SealedBox(combined: data)
        return try AES.GCM.open(box, using: try key())
    }

    // MARK: Name / path helpers

    private func safeUser(_ username: String) -> String {
        String(username.map { $0.isLetter || $0.isNumber || $0 == "_" || $0 == "." || $0 == "-" ? $0 : "_" })
    }

    private func userDir(_ username: String) -> String {
        "\(usersDir)/\(safeUser(username))"
    }

    /// Strip .conf or .conf.enc to get the profile/tunnel name, or nil if invalid.
    private func profileName(fromFile file: String) -> String? {
        let stem: String
        if file.hasSuffix(encExt) {
            stem = String(file.dropLast(encExt.count))
        } else if file.hasSuffix(plainExt) {
            stem = String(file.dropLast(plainExt.count))
        } else {
            return nil
        }
        return ProfileNameHelper.isValid(stem) ? stem : nil
    }

    // MARK: Managed-profile tracking

    private func loadManaged(_ username: String) -> [String] {
        let path = "\(userDir(username))/managed.json"
        guard let data = fm.contents(atPath: path),
              let names = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return names
    }

    private func saveManaged(_ username: String, _ names: [String]) {
        let path = "\(userDir(username))/managed.json"
        guard let data = try? JSONEncoder().encode(names) else { return }
        try? data.write(to: URL(fileURLWithPath: path))
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
    }

    func isManaged(_ username: String, _ profile: String) -> Bool {
        loadManaged(username).contains(profile)
    }

    // MARK: Enumeration

    func profiles(for username: String) -> [String] {
        guard let files = try? fm.contentsOfDirectory(atPath: userDir(username)) else { return [] }
        var names = Set<String>()
        for f in files { if let n = profileName(fromFile: f) { names.insert(n) } }
        return names.sorted()
    }

    func deletableProfiles(for username: String) -> [String] {
        let managed = Set(loadManaged(username))
        return profiles(for: username).filter { !managed.contains($0) }
    }

    /// All profiles across all users (for the heartbeat payload).
    func allProfiles() -> [String] {
        guard let userDirs = try? fm.contentsOfDirectory(atPath: usersDir) else { return [] }
        var names = Set<String>()
        for user in userDirs {
            guard let files = try? fm.contentsOfDirectory(atPath: "\(usersDir)/\(user)") else { continue }
            for f in files { if let n = profileName(fromFile: f) { names.insert(n) } }
        }
        return names.sorted()
    }

    /// Put the server-preferred profile first (mirrors Windows OrderProfiles / Linux order_profiles).
    func orderProfiles(_ profiles: [String], serverProfileName: String?) -> [String] {
        guard let serverProfileName, profiles.count >= 2,
              let idx = profiles.firstIndex(where: { $0.lowercased() == serverProfileName.lowercased() }),
              idx != 0 else { return profiles }
        var reordered = profiles
        let server = reordered.remove(at: idx)
        reordered.insert(server, at: 0)
        return reordered
    }

    private func storedPath(_ username: String, _ profile: String) -> String? {
        let enc = "\(userDir(username))/\(profile)\(encExt)"
        let plain = "\(userDir(username))/\(profile)\(plainExt)"
        if fm.fileExists(atPath: enc) { return enc }
        if fm.fileExists(atPath: plain) { return plain }
        return nil
    }

    /// Search all user dirs for a stored config matching the tunnel name (cross-user;
    /// a tunnel may be owned by AutoConnect/PendingConnect/Unknown, not the caller).
    private func storedPathByTunnel(_ tunnel: String) -> String? {
        guard let userDirs = try? fm.contentsOfDirectory(atPath: usersDir) else { return nil }
        for user in userDirs {
            for ext in [encExt, plainExt] {
                let p = "\(usersDir)/\(user)/\(tunnel)\(ext)"
                if fm.fileExists(atPath: p) { return p }
            }
        }
        return nil
    }

    // MARK: Decrypt-to-memory (macOS: never to disk)

    /// Decrypt (or read) a stored config into memory, validating the allowlist before it can
    /// reach the tunnel engine. The plaintext is returned to the caller and never written to
    /// disk — the macOS replacement for Linux's prepare_config_path()/TEMP_DIR.
    func decryptedContent(username: String, profile: String) throws -> String {
        guard let path = storedPath(username, profile) else {
            throw ConfigError.notFound(profile)
        }
        return try decryptedContent(atPath: path)
    }

    func decryptedContentByTunnel(_ tunnel: String) throws -> String {
        guard let path = storedPathByTunnel(tunnel) else { throw ConfigError.notFound(tunnel) }
        return try decryptedContent(atPath: path)
    }

    private func decryptedContent(atPath path: String) throws -> String {
        guard let data = fm.contents(atPath: path) else { throw ConfigError.notFound(path) }
        let plaintext: String
        if path.hasSuffix(encExt) {
            plaintext = String(decoding: try decrypt(data), as: UTF8.self)
        } else {
            plaintext = String(decoding: data, as: UTF8.self)
        }
        if let err = ConfigValidation.validateContent(plaintext) {
            throw ConfigError.unsafe(path, err)
        }
        return plaintext
    }

    // MARK: Write / save

    func saveConfig(username: String, profile: String, content: String, managed: Bool = false) throws {
        let dir = userDir(username)
        try fm.createDirectory(atPath: dir, withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        let encPath = "\(dir)/\(profile)\(encExt)"
        let plainPath = "\(dir)/\(profile)\(plainExt)"
        try encrypt(Data(content.utf8)).write(to: URL(fileURLWithPath: encPath))
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: encPath)
        if fm.fileExists(atPath: plainPath) { try? fm.removeItem(atPath: plainPath) }
        if managed {
            var existing = loadManaged(username)
            if !existing.contains(profile) {
                existing.append(profile)
                saveManaged(username, existing)
            }
        }
    }

    /// User-initiated delete, scoped to one user (rejects managed profiles at the caller).
    @discardableResult
    func deleteUserProfile(username: String, profile: String) -> Bool {
        var deleted = false
        for ext in [plainExt, encExt] {
            let p = "\(userDir(username))/\(profile)\(ext)"
            if fm.fileExists(atPath: p) { (try? fm.removeItem(atPath: p)).map { deleted = true } }
        }
        return deleted
    }

    /// Admin-initiated delete (mirrors Linux delete_profile): cross-user (heartbeat/poll have
    /// no interactive user to scope to), plus any unclaimed staged copy.
    @discardableResult
    func deleteProfile(_ profile: String) -> Bool {
        var deleted = false
        if let userDirs = try? fm.contentsOfDirectory(atPath: usersDir) {
            for user in userDirs {
                for ext in [plainExt, encExt] {
                    let p = "\(usersDir)/\(user)/\(profile)\(ext)"
                    if fm.fileExists(atPath: p) { (try? fm.removeItem(atPath: p)).map { deleted = true } }
                }
            }
        }
        let staged = "\(stagedDir)/\(profile)\(plainExt)"
        if fm.fileExists(atPath: staged) { try? fm.removeItem(atPath: staged) }
        return deleted
    }

    // MARK: Staged config lifecycle

    func hasStagedConfig() -> Bool {
        guard let files = try? fm.contentsOfDirectory(atPath: stagedDir) else { return false }
        return files.contains { $0.hasSuffix(plainExt) }
    }

    func stagedProfileNames() -> [String] {
        guard let files = try? fm.contentsOfDirectory(atPath: stagedDir) else { return [] }
        return files.compactMap { file in
            guard file.hasSuffix(plainExt) else { return nil }
            let stem = String(file.dropLast(plainExt.count))
            return ProfileNameHelper.isValid(stem) ? stem : nil
        }
    }

    /// `filename` is a bare profile name (no extension) — the backend's
    /// PendingConfigFileName/FileName fields never carry one. MUST append `.conf` or every
    /// staging glob (`*.conf`) silently misses the file forever.
    func saveStagedConfig(filename: String, content: String) {
        try? fm.createDirectory(atPath: stagedDir, withIntermediateDirectories: true,
                                attributes: [.posixPermissions: 0o700])
        let p = "\(stagedDir)/\(filename)\(plainExt)"
        try? Data(content.utf8).write(to: URL(fileURLWithPath: p))
        try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: p)
    }

    /// Move all staged configs into the user's dir (encrypted, marked managed). Returns the
    /// claimed profile names.
    @discardableResult
    func claimStagedConfig(username: String) -> [String] {
        guard let files = try? fm.contentsOfDirectory(atPath: stagedDir) else { return [] }
        var claimed: [String] = []
        for file in files where file.hasSuffix(plainExt) {
            let path = "\(stagedDir)/\(file)"
            let name = ConfigValidation.sanitizeProfileName(file)
            guard let data = fm.contents(atPath: path) else { continue }
            let content = String(decoding: data, as: UTF8.self)
            do {
                try saveConfig(username: username, profile: name, content: content, managed: true)
                try? fm.removeItem(atPath: path)
                claimed.append(name)
            } catch {
                // leave staged file for next attempt
            }
        }
        return claimed
    }

    // MARK: AllowedIPs conflict checking (in-memory decrypt only)

    private func readAllowedIps(atPath path: String) -> [String] {
        guard let data = fm.contents(atPath: path) else { return [] }
        let content: String
        if path.hasSuffix(encExt) {
            guard let plain = try? decrypt(data) else { return [] }
            content = String(decoding: plain, as: UTF8.self)
        } else {
            content = String(decoding: data, as: UTF8.self)
        }
        var result: [String] = []
        for raw in content.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard line.lowercased().hasPrefix("allowedips"), let eq = line.firstIndex(of: "=") else { continue }
            for part in line[line.index(after: eq)...].split(separator: ",") {
                let cidr = part.trimmingCharacters(in: .whitespaces)
                if !cidr.isEmpty { result.append(cidr) }
            }
        }
        return result
    }

    /// Returns the name of the first active tunnel whose AllowedIPs overlap the new profile's,
    /// or nil. A full-tunnel (0.0.0.0/0) in either config blocks all coexistence. Decrypts in
    /// memory only. Mirrors Windows FindAllowedIpsConflict / Linux find_allowed_ips_conflict.
    func findAllowedIpsConflict(newProfileUser: String, newProfile: String, activeTunnels: [String]) -> String? {
        guard let newPath = storedPath(newProfileUser, newProfile) else { return nil }
        let newCidrs = readAllowedIps(atPath: newPath)
        guard !newCidrs.isEmpty, !activeTunnels.isEmpty else { return nil }
        let isFullNew = newCidrs.contains { $0.hasPrefix("0.0.0.0/0") }
        for name in activeTunnels {
            guard let stored = storedPathByTunnel(name) else { continue }
            let existing = readAllowedIps(atPath: stored)
            if existing.contains(where: { $0.hasPrefix("0.0.0.0/0") }) || isFullNew { return name }
            for a in newCidrs {
                for b in existing where cidrsOverlapV4(a, b) { return name }
            }
        }
        return nil
    }

    private func readAddress(atPath path: String) -> [String] {
        guard let data = fm.contents(atPath: path) else { return [] }
        let content: String
        if path.hasSuffix(encExt) {
            guard let plain = try? decrypt(data) else { return [] }
            content = String(decoding: plain, as: UTF8.self)
        } else {
            content = String(decoding: data, as: UTF8.self)
        }
        for raw in content.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard line.lowercased().hasPrefix("address"), let eq = line.firstIndex(of: "=") else { continue }
            return line[line.index(after: eq)...].split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
        }
        return []
    }

    /// Returns a human-readable description of a conflict between this machine's own local LAN
    /// and either (A) an explicit remote route in the new profile's AllowedIPs, (B) a remote LAN
    /// CIDR reported by the backend for a full-tunnel profile (`remoteLanCidrs`), or (C) the VPN
    /// IP the new profile would assign to this machine — or nil if there is none. Default-route
    /// AllowedIPs entries (0.0.0.0/0) are excluded from (A), matching Windows/Linux. Mirrors
    /// Windows FindLocalLanConflict / Linux find_local_lan_conflict.
    func findLocalLanConflict(newProfileUser: String, newProfile: String, remoteLanCidrs: [String]) -> String? {
        let localCidrs = TrustedNetworkDetector.localLanCidrs()
        guard !localCidrs.isEmpty, let newPath = storedPath(newProfileUser, newProfile) else { return nil }

        let remoteCidrs = readAllowedIps(atPath: newPath).filter { !$0.hasPrefix("0.0.0.0/0") } + remoteLanCidrs
        let assignedAddresses = readAddress(atPath: newPath)

        for local in localCidrs {
            for remote in remoteCidrs where cidrsOverlapV4(local, remote) {
                return "Your local network (\(local)) overlaps with a network routed through this VPN " +
                       "(\(remote)). Connecting would make local devices unreachable or misroute traffic " +
                       "meant for the remote network. Contact your administrator or change your local " +
                       "network before connecting."
            }
            for addr in assignedAddresses where cidrsOverlapV4(local, addr) {
                return "Your local network (\(local)) overlaps with the VPN address assigned to this " +
                       "device (\(addr)). Connecting would make this device unreachable on your local " +
                       "network. Contact your administrator before connecting."
            }
        }
        return nil
    }
}

enum ConfigError: Error, CustomStringConvertible {
    case notFound(String)
    case unsafe(String, String)

    var description: String {
        switch self {
        case .notFound(let p): return "Config not found: \(p)"
        case .unsafe(let p, let why): return "Refusing unsafe config '\(p)': \(why)"
        }
    }
}

// MARK: - IPv4 CIDR overlap (mirrors Linux _cidrs_overlap)

func parseCidrV4(_ cidr: String) -> (net: UInt32, prefix: Int)? {
    let parts = cidr.split(separator: "/")
    let addr = String(parts[0])
    let prefixLen = parts.count > 1 ? Int(parts[1]) ?? 32 : 32
    let octets = addr.split(separator: ".")
    guard octets.count == 4 else { return nil }
    var value: UInt32 = 0
    for o in octets {
        guard let n = UInt32(o), n <= 255 else { return nil }
        value = (value << 8) | n
    }
    let mask: UInt32 = prefixLen == 0 ? 0 : (0xFFFF_FFFF << (32 - prefixLen))
    return (value & mask, prefixLen)
}

func cidrsOverlapV4(_ a: String, _ b: String) -> Bool {
    guard let (aNet, aPfx) = parseCidrV4(a), let (bNet, bPfx) = parseCidrV4(b) else { return false }
    let minPfx = min(aPfx, bPfx)
    let mask: UInt32 = minPfx == 0 ? 0 : (0xFFFF_FFFF << (32 - minPfx))
    return (aNet & mask) == (bNet & mask)
}
