// Valenius.app — menu bar app entry point. Mirrors Clients/Linux/tray + Windows TrayApp.
// Built as a SwiftPM executable; menu-bar (no Dock/menu) behavior via .accessory policy.
//
// @main + @MainActor main() so the whole AppKit setup is statically main-actor-isolated
// (constructing the @MainActor AppState/AppDelegate from a plain top-level main.swift is a
// concurrency error). Not a top-level-code file — hence the @main entry point.

import AppKit

@main
enum ValeniusMain {
    @MainActor
    static func main() {
        // Single-instance guard (mirrors the Linux tray's flock and Windows' named mutex). As a
        // bare executable we can't rely on a bundle id, so use an advisory file lock; held for
        // the process lifetime.
        let lockFd = acquireSingleInstanceLock()

        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory) // no Dock icon, no main menu — a menu bar agent
        app.run()

        _ = lockFd
    }
}

// MARK: - Single-instance lock

func acquireSingleInstanceLock() -> Int32 {
    let path = "\(NSTemporaryDirectory())com.stranto.valenius.app.lock"
    let fd = open(path, O_CREAT | O_RDWR, 0o644)
    if fd < 0 { return -1 }
    if flock(fd, LOCK_EX | LOCK_NB) != 0 {
        // Another instance holds the lock — exit quietly (mirrors Linux tray / Windows mutex).
        FileHandle.standardError.write("Valenius.app is already running.\n".data(using: .utf8)!)
        exit(0)
    }
    return fd
}
