// HTTP client for the Valenius backend API — mirrors Clients/Linux/daemon/backend.py.
// Requests use PascalCase JSON and the X-Api-Key header, matching Windows/Linux.

import CryptoKit
import Darwin
import Foundation

let daemonVersion = "0.1.0"

enum UpdaterError: Error, CustomStringConvertible {
    case badUrl(String)
    case downloadFailed
    case shaMismatch(expected: String, actual: String)

    var description: String {
        switch self {
        case .badUrl(let u): return "bad download URL: \(u)"
        case .downloadFailed: return "download failed"
        case .shaMismatch(let e, let a):
            return "SHA-256 mismatch — expected \(e.prefix(16))… got \(a.prefix(16))…; update aborted"
        }
    }
}

/// A backend-rotated API key is persisted here (root-only) so it survives a daemon
/// restart/upgrade. On startup a persisted key takes precedence over the one from
/// appsettings.json, so a fresh install bootstraps from appsettings while a rotated
/// deployment keeps the new key.
private let apiKeyPath = "\(appSupportDir)/apikey"

/// A user-entered backend URL (first-run setup dialog) is persisted here (root-only), taking
/// precedence over appsettings.json — mirrors Windows' backend.dat / BackendUrlProvider. Kept
/// separate from appsettings so the persisted user choice always wins over (and is never
/// clobbered by) the bundled default/template.
private let backendUrlPath = "\(appSupportDir)/backend.dat"

actor BackendClient {
    private(set) var baseUrl: String
    private var apiKey: String
    private var channel = "stable" // auto-update channel, refreshed from the heartbeat
    private let session = URLSession(configuration: .ephemeral)

    init(baseUrl: String, apiKey: String) {
        let fallback = baseUrl.hasSuffix("/") ? String(baseUrl.dropLast()) : baseUrl
        if let persisted = try? String(contentsOfFile: backendUrlPath, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
           !persisted.isEmpty {
            self.baseUrl = persisted
        } else {
            self.baseUrl = fallback
        }
        if let persisted = try? String(contentsOfFile: apiKeyPath, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
           !persisted.isEmpty {
            self.apiKey = persisted
        } else {
            self.apiKey = apiKey
        }
    }

    /// True once a non-empty backend URL is available (from backend.dat or appsettings.json).
    var isConfigured: Bool { !baseUrl.isEmpty }

    /// Set the backend URL from a user-entered DNS host (scheme optional). Normalizes to
    /// `https://host`, persists it, and switches immediately. Returns the normalized URL, or
    /// nil if the input has no usable host. Mirrors Windows BackendUrlProvider.Set.
    func setBaseUrl(_ dnsOrUrl: String?) -> String? {
        let normalized = Self.normalizeUrl(dnsOrUrl)
        guard !normalized.isEmpty else { return nil }
        baseUrl = normalized
        do {
            try FileManager.default.createDirectory(
                atPath: appSupportDir, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700]
            )
            try normalized.write(toFile: backendUrlPath, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: backendUrlPath)
            log("Backend URL set to \(normalized) and persisted.")
        } catch {
            logError("Could not persist backend URL; using it for this session only: \(error)")
        }
        return normalized
    }

    /// Coerce a user-entered DNS/URL to the canonical https form the rest of the client
    /// assumes: strip any scheme the user typed/pasted, drop path/query and surrounding
    /// whitespace/slashes, then prepend https://. Returns "" when no host remains. Mirrors
    /// Windows BackendUrlProvider.Normalize.
    static func normalizeUrl(_ input: String?) -> String {
        var s = (input ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !s.isEmpty else { return "" }
        if let schemeRange = s.range(of: "://") {
            s = String(s[schemeRange.upperBound...])
        }
        while s.hasPrefix("/") { s.removeFirst() }
        if let cut = s.firstIndex(where: { "/?#".contains($0) }) {
            s = String(s[..<cut])
        }
        s = s.trimmingCharacters(in: .whitespaces)
        while s.hasSuffix(".") { s.removeLast() }
        return s.isEmpty ? "" : "https://" + s
    }

    func currentApiKey() -> String { apiKey }

    func setChannel(_ channel: String?) {
        self.channel = (channel?.lowercased() == "beta") ? "beta" : "stable"
    }

    /// Adopt a backend-rotated key (heartbeat ClientApiKey): switch + persist so it
    /// survives a restart. No-op if empty or unchanged. Only sent by the backend while
    /// this client is still on the previous key during a rotation grace window.
    func setApiKey(_ newKey: String?) {
        guard let newKey, !newKey.isEmpty, newKey != apiKey else { return }
        apiKey = newKey
        do {
            try FileManager.default.createDirectory(
                atPath: appSupportDir, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700]
            )
            try newKey.write(toFile: apiKeyPath, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: apiKeyPath)
            log("Adopted rotated client API key from backend.")
        } catch {
            logError("Could not persist rotated API key: \(error)")
        }
    }

    private func request(method: String, path: String, body: [String: Any]? = nil, timeout: TimeInterval = 15) async -> [String: Any]? {
        guard !baseUrl.isEmpty, let url = URL(string: baseUrl + path) else { return nil }
        var req = URLRequest(url: url, timeoutInterval: timeout)
        req.httpMethod = method
        req.setValue(apiKey, forHTTPHeaderField: "X-Api-Key")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("Valenius-macOS/\(daemonVersion)", forHTTPHeaderField: "User-Agent")
        if let body {
            req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }
        do {
            let (data, response) = try await session.data(for: req)
            guard let http = response as? HTTPURLResponse else { return nil }
            guard (200..<300).contains(http.statusCode) else {
                log("HTTP \(http.statusCode) \(method) \(path)")
                return nil
            }
            if data.isEmpty { return [:] }
            return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        } catch {
            log("\(method) \(path) failed: \(error)")
            return nil
        }
    }

    func register(clientId: UUID, hostname: String, username: String, profiles: [String], trayRunning: Bool) async -> [String: Any]? {
        await request(method: "POST", path: "/api/clients/register", body: [
            "ClientKey": clientId.uuidString,
            "Hostname": hostname,
            "HostSid": "",
            "Username": username,
            "UserSid": "",
            "Version": daemonVersion,
            "TrayRunning": trayRunning,
            "Profiles": profiles,
            "Platform": "macos",
        ])
    }

    /// The backend holds the connection for up to 55 s and returns as soon as an admin
    /// action changes this client's state (mirrors Windows/Linux long-poll). Timeout must
    /// exceed the server's hold time.
    func poll(clientId: UUID, trayRunning: Bool) async -> [String: Any]? {
        await request(
            method: "GET",
            path: "/api/clients/poll?clientKey=\(clientId.uuidString)&trayRunning=\(trayRunning)",
            timeout: 65
        )
    }

    /// Best-effort: tells the backend to immediately drop this client from the online
    /// presence tracker (app closed / daemon stopping).
    func notifyOffline(clientId: UUID) async {
        _ = await request(method: "POST", path: "/api/clients/offline?clientKey=\(clientId.uuidString)")
    }

    func logEvent(clientId: UUID, eventType: String, username: String, tunnelName: String, lanIp: String = "", wanIp: String = "") async {
        _ = await request(method: "POST", path: "/api/clients/event", body: [
            "ClientKey": clientId.uuidString,
            "EventType": eventType,
            "Username": username,
            "TunnelName": tunnelName,
            "LanIp": lanIp,
            "WanIp": wanIp,
        ])
    }

    func getPendingConfig(clientId: UUID) async -> [String: Any]? {
        await request(method: "GET", path: "/api/clients/pending-config?clientKey=\(clientId.uuidString)")
    }

    /// Per-OS release stream: {version, downloadUrl, sha256, releaseNotes}. macOS is independent
    /// of the Windows/Linux streams. ?channel=beta selects beta (falls back to stable if none).
    func getVersion() async -> [String: Any]? {
        var path = "/api/version/macos"
        if channel == "beta" { path += "?channel=beta" }
        return await request(method: "GET", path: path)
    }

    /// Download `url` to `destination`, verifying its SHA-256 before returning. Throws on
    /// mismatch (tamper/corruption guard — the update is aborted, never installed).
    func downloadVerified(url: String, expectedSha256: String, to destination: URL) async throws {
        guard let src = URL(string: url.hasPrefix("http") ? url : baseUrl + url) else {
            throw UpdaterError.badUrl(url)
        }
        var req = URLRequest(url: src, timeoutInterval: 300)
        req.setValue("Valenius-macOS/\(daemonVersion)", forHTTPHeaderField: "User-Agent")
        let (tempFile, response) = try await session.download(for: req)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw UpdaterError.downloadFailed
        }
        let data = try Data(contentsOf: tempFile)
        let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard actual.caseInsensitiveCompare(expectedSha256) == .orderedSame else {
            throw UpdaterError.shaMismatch(expected: expectedSha256, actual: actual)
        }
        try? FileManager.default.removeItem(at: destination)
        try data.write(to: destination)
    }

    /// POST /api/clients/mfa/enroll-confirm — returns true on a 2xx (TOTP code accepted).
    /// Mirrors Linux confirm_mfa_enrollment. Distinct from `request` because we need to tell a
    /// rejected code (non-2xx → false) apart from a transport failure, both of which `request`
    /// collapses to nil.
    func confirmMfaEnrollment(clientId: UUID, code: String) async -> Bool {
        guard let url = URL(string: baseUrl + "/api/clients/mfa/enroll-confirm") else { return false }
        var req = URLRequest(url: url, timeoutInterval: 15)
        req.httpMethod = "POST"
        req.setValue(apiKey, forHTTPHeaderField: "X-Api-Key")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("Valenius-macOS/\(daemonVersion)", forHTTPHeaderField: "User-Agent")
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["ClientKey": clientId.uuidString, "Code": code])
        guard let (_, response) = try? await session.data(for: req),
              let http = response as? HTTPURLResponse else { return false }
        return (200..<300).contains(http.statusCode)
    }

    /// POST a gzipped, redacted diagnostic bundle to /api/clients/logs as multipart/form-data.
    /// Hand-built form (mirrors the Linux client). `trigger` is "Admin" (heartbeat pull) or
    /// "User" (SendLogs action).
    func uploadLogs(clientId: UUID, gzData: Data, trigger: String) async -> Bool {
        guard let url = URL(string: baseUrl + "/api/clients/logs") else { return false }
        let boundary = "----ValeniusBoundary\(UUID().uuidString)"
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        field("clientKey", clientId.uuidString)
        field("trigger", trigger)
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"valenius-logs.log.gz\"\r\nContent-Type: application/gzip\r\n\r\n".utf8))
        body.append(gzData)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))

        var req = URLRequest(url: url, timeoutInterval: 30)
        req.httpMethod = "POST"
        req.setValue(apiKey, forHTTPHeaderField: "X-Api-Key")
        req.setValue("Valenius-macOS/\(daemonVersion)", forHTTPHeaderField: "User-Agent")
        req.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        req.httpBody = body
        guard let (_, response) = try? await session.data(for: req),
              let http = response as? HTTPURLResponse else { return false }
        return (200..<300).contains(http.statusCode)
    }

    /// WAN IP fetched from api4.ipify.org **before** the tunnel comes up (mirrors all clients).
    func getWanIp() async -> String {
        guard let url = URL(string: "https://api4.ipify.org") else { return "" }
        var req = URLRequest(url: url, timeoutInterval: 5)
        req.setValue("Valenius-macOS/\(daemonVersion)", forHTTPHeaderField: "User-Agent")
        guard let (data, _) = try? await session.data(for: req) else { return "" }
        return String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// Best-effort first non-loopback IPv4 (UDP-connect trick to 8.8.8.8, read local endpoint).
/// Mirrors Windows' NetworkDetector / Linux's get_lan_ip.
func getLanIp() -> String {
    let fd = socket(AF_INET, SOCK_DGRAM, 0)
    guard fd >= 0 else { return "" }
    defer { close(fd) }
    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = in_port_t(65530).bigEndian
    inet_pton(AF_INET, "8.8.8.8", &addr.sin_addr)
    let connected = withUnsafePointer(to: &addr) { ptr in
        ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
    }
    guard connected == 0 else { return "" }
    var local = sockaddr_in()
    var len = socklen_t(MemoryLayout<sockaddr_in>.size)
    let got = withUnsafeMutablePointer(to: &local) { ptr in
        ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) }
    }
    guard got == 0 else { return "" }
    var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
    inet_ntop(AF_INET, &local.sin_addr, &buf, socklen_t(INET_ADDRSTRLEN))
    return String(cString: buf)
}
