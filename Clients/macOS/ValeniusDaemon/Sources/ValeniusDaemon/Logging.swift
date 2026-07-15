// Central logger — subsystem matches the concept doc's diagnostics collector predicate
// ('subsystem == "com.stranto.valenius"'), so M6's `log show` collector picks this up
// with no further wiring.

import Foundation
import os

let daemonLogger = Logger(subsystem: "com.stranto.valenius", category: "daemon")

// Also mirror to stderr: os.Logger .info entries are memory-only and often invisible to
// `log show`/`log stream`, and the LaunchDaemon plist routes stderr to daemon.err.log — so
// stderr is where operational output is reliably visible (dev + production). Mirrors the app's
// appLog.
func log(_ message: String) {
    daemonLogger.info("\(message, privacy: .public)")
    FileHandle.standardError.write("[valeniusd] \(message)\n".data(using: .utf8)!)
}

func logError(_ message: String) {
    daemonLogger.error("\(message, privacy: .public)")
    FileHandle.standardError.write("[valeniusd] ERROR \(message)\n".data(using: .utf8)!)
}
