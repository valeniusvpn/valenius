// Auto-updater — port of Clients/Linux/daemon/updater.py, but with the macOS detached-install
// mechanism (the Windows lesson): the pkg's postinstall restarts the
// daemon, so `installer` must NOT be a child of the daemon — it would be torn down mid-install
// and the update would silently fail (download+verify succeed, nothing installs).
//
// Flow:
//   1. GET /api/version/macos?channel=  → compare version strings
//   2. Download the .pkg, VERIFY SHA-256 before touching anything (tamper/corruption guard)
//   3. Hand off to a one-shot LaunchDaemon (unique label + script name PER RUN — a fixed name
//      could be a poisoned leftover) that runs `installer -pkg … -target /`, then boots itself
//      out and self-deletes. launchd owns it, so it survives the daemon stop/restart.
//
// **Never break this path** — a regression strands every client on its current version, since
// the fix can only reach them THROUGH the mechanism that just broke. Any change must be verified
// by a real unattended self-update (old installed → newer published → daemon updates itself).

import Foundation

private let updatesDir = "\(appSupportDir)/updates"

actor Updater {
    private let backend: BackendClient
    private var applying = false

    init(backend: BackendClient) {
        self.backend = backend
    }

    struct CheckResult {
        var available: Bool
        var latest: String
        var url: String?
        var sha256: String?
    }

    func check() async -> CheckResult {
        guard let data = await backend.getVersion() else {
            return CheckResult(available: false, latest: daemonVersion, url: nil, sha256: nil)
        }
        let j = JsonObject(data)
        let latest = j.string("version") ?? ""
        let url = j.string("downloadUrl")
        let sha = j.string("sha256")
        let available = versionGreater(latest, than: daemonVersion) && url?.isEmpty == false && sha?.isEmpty == false
        return CheckResult(available: available, latest: latest, url: url, sha256: sha)
    }

    /// Check and, if a newer version is published, download + verify + detached-install it.
    func checkAndApply() async {
        if applying { return }
        let result = await check()
        guard result.available, let url = result.url, let sha = result.sha256 else { return }

        applying = true
        defer { applying = false }
        log("Update available: \(daemonVersion) → \(result.latest) — downloading")

        let pkgPath = URL(fileURLWithPath: "\(updatesDir)/Valenius-\(result.latest).pkg")
        do {
            try? FileManager.default.createDirectory(atPath: updatesDir, withIntermediateDirectories: true,
                                                     attributes: [.posixPermissions: 0o700])
            try await backend.downloadVerified(url: url, expectedSha256: sha, to: pkgPath)
            log("Downloaded \(pkgPath.lastPathComponent) — SHA-256 verified; launching detached install")
            try launchDetachedInstall(pkgPath: pkgPath.path, version: result.latest)
        } catch {
            logError("Update failed: \(error)")
        }
    }

    /// Hand the install to a one-shot LaunchDaemon so it outlives the daemon restart the pkg
    /// triggers. Unique label + script name per run (never a fixed name — a leftover could be
    /// read-only / hostile-DACL and strand the client, the Windows run-update.cmd lesson).
    private func launchDetachedInstall(pkgPath: String, version: String) throws {
        let stamp = "\(Int(Date().timeIntervalSince1970))"
        let label = "com.stranto.valenius.update.\(stamp)"
        let scriptPath = "\(updatesDir)/run-update-\(stamp).sh"
        let plistPath = "/Library/LaunchDaemons/\(label).plist"
        let installLog = "\(updatesDir)/install.log"

        let script = """
        #!/bin/bash
        # One-shot Valenius updater — installs the verified pkg, then removes itself.
        echo "=== Valenius update to \(version) at $(date) ===" >> "\(installLog)"
        /usr/sbin/installer -pkg "\(pkgPath)" -target / >> "\(installLog)" 2>&1
        rc=$?
        echo "installer exit=$rc" >> "\(installLog)"
        /bin/launchctl bootout system/\(label) 2>/dev/null
        /bin/rm -f "\(plistPath)" "\(pkgPath)" "\(scriptPath)"
        exit 0
        """
        try script.write(toFile: scriptPath, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: scriptPath)

        let plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key><string>\(label)</string>
            <key>ProgramArguments</key><array><string>/bin/bash</string><string>\(scriptPath)</string></array>
            <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """
        try plist.write(toFile: plistPath, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: plistPath)

        // bootstrap into the system domain — launchd (not this daemon) now owns the install job.
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        proc.arguments = ["bootstrap", "system", plistPath]
        try proc.run()
        proc.waitUntilExit()
        if proc.terminationStatus != 0 {
            throw UpdaterError.downloadFailed // reuse: surfaces as a logged failure
        }
    }

    /// Best-effort cleanup of stale update artifacts at startup (mirrors the Windows
    /// CleanupOldLaunchScripts). A crashed prior run could leave a script/plist behind.
    static func cleanupStaleArtifacts() {
        guard let files = try? FileManager.default.contentsOfDirectory(atPath: updatesDir) else { return }
        for f in files where f.hasPrefix("run-update-") && f.hasSuffix(".sh") {
            try? FileManager.default.removeItem(atPath: "\(updatesDir)/\(f)")
        }
    }
}

/// Numeric dotted-version compare (mirrors Linux _version_gt): "0.2.1" > "0.1.9".
func versionGreater(_ a: String, than b: String) -> Bool {
    func parts(_ v: String) -> [Int] {
        let p = v.split(separator: ".").map { Int($0) ?? 0 }
        return p.isEmpty ? [0] : p
    }
    let (pa, pb) = (parts(a), parts(b))
    for i in 0..<max(pa.count, pb.count) {
        let x = i < pa.count ? pa[i] : 0
        let y = i < pb.count ? pb[i] : 0
        if x != y { return x > y }
    }
    return false
}
