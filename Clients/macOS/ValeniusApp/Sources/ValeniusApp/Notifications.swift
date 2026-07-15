// User notifications (staged-config claims, etc.). UNUserNotificationCenter requires a
// bundled app with a bundle identifier — calling it from a bare dev executable traps — so
// this is a no-op until the app runs as Valenius.app (packaged in M7). Mirrors the balloon
// notifications the Windows/Linux trays show on config claim.

import Foundation
import UserNotifications

enum Notifications {
    private static let available = Bundle.main.bundleIdentifier != nil
    private static var requested = false

    static func post(title: String, body: String) {
        guard available else {
            appLog("notify (suppressed, unbundled): \(title) — \(body)")
            return
        }
        let center = UNUserNotificationCenter.current()
        if !requested {
            requested = true
            center.requestAuthorization(options: [.alert, .sound]) { _, _ in }
        }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        center.add(request)
    }
}
