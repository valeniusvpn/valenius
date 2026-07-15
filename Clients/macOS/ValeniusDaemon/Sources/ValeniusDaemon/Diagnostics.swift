// Collects Valenius-only diagnostic logs into a gzipped, redacted text bundle — port of
// Clients/Linux/daemon/diagnostics.py.
//
// Fixed allowlist: a summary header + this subsystem's unified-log entries (`log show`, the
// macOS analogue of journalctl) + the updater install.log. Secrets (API key, WireGuard
// private/preshared keys, TOTP secrets, tokens) are redacted before the bytes ever leave the
// machine. No other system data is gathered.

import Darwin
import Foundation

private let installLogPath = "\(appSupportDir)/updates/install.log"

enum Diagnostics {
    static func collectBundle(apiKey: String?) -> Data {
        let sections: [(String, String)] = [
            ("Summary", summary()),
            ("log show --predicate subsystem==com.stranto.valenius (last 48h)", unifiedLog()),
            ("updater install.log", installLog()),
        ]
        let text = sections.map { "===== \($0.0) =====\n\($0.1)" }.joined(separator: "\n\n")
        return gzip(Data(redact(text, apiKey: apiKey).utf8))
    }

    private static func summary() -> String {
        let info = ProcessInfo.processInfo
        return [
            "Collected: \(ISO8601DateFormatter().string(from: Date()))",
            "Host:      \(info.hostName)",
            "OS:        \(info.operatingSystemVersionString)",
            "Client:    \(daemonVersion)",
        ].joined(separator: "\n")
    }

    private static func unifiedLog() -> String {
        let out = runCapturing("/usr/bin/log", [
            "show", "--last", "48h",
            "--predicate", "subsystem == \"com.stranto.valenius\"",
            "--style", "compact",
        ], timeout: 30)
        // Cap the volume the same spirit as Linux's `-n 3000` (log show has no line flag).
        let lines = out.split(separator: "\n")
        let tail = lines.suffix(3000)
        return tail.isEmpty ? "[no output]" : tail.joined(separator: "\n")
    }

    private static func installLog() -> String {
        guard let data = FileManager.default.contents(atPath: installLogPath) else {
            return "[not present]"
        }
        return String(decoding: data, as: UTF8.self)
    }

    // MARK: Redaction (same patterns as Linux _redact)

    /// Internal (not private) so the test target can verify secrets are stripped — this is the
    /// load-bearing guarantee that nothing sensitive leaves the machine.
    static func redact(_ input: String, apiKey: String?) -> String {
        var text = input
        if let apiKey, !apiKey.isEmpty {
            text = text.replacingOccurrences(of: apiKey, with: "[REDACTED-APIKEY]")
        }
        // WireGuard private / preshared keys (PrivateKey/PresharedKey = ...).
        text = replace(text, pattern: #"(?im)^(\s*(?:PrivateKey|PresharedKey))\s*=\s*.+$"#, template: "$1 = [REDACTED]")
        // Any bare base64 32-byte key (43 chars + '=').
        text = replace(text, pattern: #"\b[A-Za-z0-9+/]{43}="#, template: "[REDACTED-KEY]")
        // secret / token / authorization / bearer / apikey values. We redact the REST OF THE
        // LINE ([^\r\n]+) rather than the single token the Linux `\S+` grabs: an
        // "Authorization: Bearer <jwt>" value is two whitespace-separated tokens, so `\S+`
        // would leave the actual credential behind. Strictly more thorough — never leaks less.
        text = replace(text, pattern: #"(?i)\b(secret|token|authorization|bearer|apikey|x-api-key)\b\s*[:=]\s*[^\r\n]+"#, template: "$1=[REDACTED]")
        return text
    }

    private static func replace(_ text: String, pattern: String, template: String) -> String {
        guard let re = try? NSRegularExpression(pattern: pattern) else { return text }
        let range = NSRange(text.startIndex..., in: text)
        return re.stringByReplacingMatches(in: text, range: range, withTemplate: template)
    }

    // MARK: gzip (via /usr/bin/gzip for real RFC-1952 framing the backend decompresses)

    private static func gzip(_ input: Data) -> Data {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/gzip")
        proc.arguments = ["-c"]
        let stdin = Pipe(), stdout = Pipe()
        proc.standardInput = stdin
        proc.standardOutput = stdout
        proc.standardError = FileHandle.nullDevice
        do {
            try proc.run()
            // Write on a background thread to avoid a pipe-buffer deadlock on large bundles.
            let writer = DispatchQueue(label: "com.stranto.valenius.gzip")
            writer.async {
                stdin.fileHandleForWriting.write(input)
                stdin.fileHandleForWriting.closeFile()
            }
            let out = stdout.fileHandleForReading.readDataToEndOfFile()
            proc.waitUntilExit()
            return out
        } catch {
            logError("gzip failed: \(error)")
            return input // last resort: send uncompressed rather than nothing
        }
    }
}

private func runCapturing(_ path: String, _ args: [String], timeout: TimeInterval) -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = FileHandle.nullDevice
    do {
        try p.run()
        let killer = DispatchWorkItem { if p.isRunning { p.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: killer)
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        killer.cancel()
        return String(decoding: data, as: UTF8.self)
    } catch {
        return "[unavailable: \(error)]"
    }
}
