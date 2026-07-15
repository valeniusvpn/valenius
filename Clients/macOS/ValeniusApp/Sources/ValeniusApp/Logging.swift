// App logger — unified log under the same subsystem as the daemon so M6's diagnostics
// collector ('subsystem == "com.stranto.valenius"') picks up both. Also mirrored to stderr
// so a foreground/dev launch (`.build/debug/ValeniusApp`) shows activity without Console.

import Foundation
import os

let appLogger = Logger(subsystem: "com.stranto.valenius", category: "app")

func appLog(_ message: String) {
    appLogger.info("\(message, privacy: .public)")
    FileHandle.standardError.write("[app] \(message)\n".data(using: .utf8)!)
}
