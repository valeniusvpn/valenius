// Status-item controller: owns the NSStatusItem, the popover hosting the SwiftUI PopupView,
// the 5 s poll timer (paused while the popover is open, like Windows/Linux), and the
// upload/about/quit actions.

import AppKit
import Combine
import Darwin
import SwiftUI
import UniformTypeIdentifiers
import ValeniusShared

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let popover = NSPopover()
    private let state = AppState()
    private var pollTimer: Timer?
    private var aboutWindow: NSWindow?      // single-instance About (Windows invariant)
    private var mfaEnrollWindow: NSWindow?  // single-instance enrollment dialog
    private var backendUrlWindow: NSWindow? // single-instance first-run server-address dialog
    /// Set once the backend-URL prompt has auto-shown, so the background poll doesn't keep
    /// reopening it if the user cancels (mirrors Windows TrayApplicationContext._backendPromptAutoShown).
    private var backendUrlAutoShown = false
    private var cancellables = Set<AnyCancellable>()

    private static let pollInterval: TimeInterval = 5

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = statusIcon(connected: false)
            button.action = #selector(togglePopover(_:))
            button.target = self
        }

        popover.behavior = .transient
        popover.contentViewController = NSHostingController(rootView:
            PopupView(
                state: state,
                onUploadConfig: { [weak self] in self?.uploadConfig() },
                onAbout: { [weak self] in self?.showAbout() },
                onUnlockMfa: { url in if let u = URL(string: url) { NSWorkspace.shared.open(u) } },
                onQuit: { [weak self] in self?.quit() }
            )
        )

        // Auto-present / dismiss the MFA enrollment dialog as the server opens/closes the window.
        state.$status
            .map(\.mfaEnrollmentOpen)
            .removeDuplicates()
            .sink { [weak self] open in
                if open { self?.showMfaEnroll() } else { self?.closeMfaEnroll() }
            }
            .store(in: &cancellables)

        // Auto-present the first-run server-address dialog once (fresh install/dev-run with no
        // BackendUrl configured), and dismiss it once a URL is saved. Mirrors Windows
        // TrayApplicationContext.UpdateUi's BackendUnconfigured handling.
        state.$status
            .map(\.backendUnconfigured)
            .removeDuplicates()
            .sink { [weak self] unconfigured in
                guard let self else { return }
                if unconfigured {
                    if !self.backendUrlAutoShown { self.showBackendUrl() }
                } else {
                    self.closeBackendUrl()
                }
            }
            .store(in: &cancellables)

        state.refresh()
        startPolling()
    }

    func applicationWillTerminate(_ notification: Notification) {
        IpcClient.notifyOffline()
    }

    // MARK: Polling

    private func startPolling() {
        // Timer fires on the main run loop; assumeIsolated lets us touch main-actor state
        // without a hop (the closure is otherwise treated as @Sendable/nonisolated).
        pollTimer = Timer.scheduledTimer(withTimeInterval: Self.pollInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, !self.popover.isShown else { return }
                self.state.refresh()
                self.updateIcon()
            }
        }
    }

    private func updateIcon() {
        let connected = state.status.isConnected
        statusItem.button?.image = statusIcon(connected: connected)
    }

    /// Menu bar icon: the branded Valenius shield (connected variant carries a green check),
    /// bundled into the app's Resources by build-pkg.sh. Rendered in colour (not a template)
    /// since the logo is multi-colour. Falls back to an SF Symbol when running unbundled (dev,
    /// `.build/debug` — no Resources dir).
    private func statusIcon(connected: Bool) -> NSImage? {
        let resource = connected ? "MenuBarConnected" : "MenuBarDisconnected"
        if let url = Bundle.main.url(forResource: resource, withExtension: "png"),
           let image = NSImage(contentsOf: url) {
            image.size = NSSize(width: 18, height: 18) // menu-bar height; retains full-res rep for retina
            image.isTemplate = false
            return image
        }
        let name = connected ? "shield.lefthalf.filled" : "shield"
        let fallback = NSImage(systemSymbolName: name, accessibilityDescription: "Valenius")
        fallback?.isTemplate = true
        return fallback
    }

    // MARK: Popover

    @objc private func togglePopover(_ sender: Any?) {
        guard let button = statusItem.button else { return }
        // Not configured yet: the status item's job is to collect the server address, so
        // prompt for it instead of showing the (empty) popover. Mirrors Windows ShowPopupAsync.
        if state.status.backendUnconfigured {
            showBackendUrl()
            return
        }
        if popover.isShown {
            popover.performClose(sender)
        } else {
            state.refresh(sync: true) // live refresh on open (fires a heartbeat), like Windows SyncStatus
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    // MARK: Actions

    private func uploadConfig() {
        popover.performClose(nil)
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "conf") ?? .data]
        panel.allowsMultipleSelection = false
        panel.message = "Choose a WireGuard .conf file to upload"
        guard panel.runModal() == .OK, let url = panel.url,
              let content = try? String(contentsOf: url, encoding: .utf8) else { return }
        let profile = ProfileNameHelper.sanitize(url.lastPathComponent)
        state.uploadConfig(profile: profile, content: content)
    }

    private func showAbout() {
        popover.performClose(nil)
        if let existing = aboutWindow {   // single-instance: activate instead of re-create
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let backend = state.status.backendUrl ?? "—"
        let reachable = state.status.backendReachable
        let hosting = NSHostingController(rootView: AboutView(
            version: appVersion, backendUrl: backend, reachable: reachable,
            onSendLogs: { [weak self] in self?.state.sendLogs() }
        ))
        let window = NSWindow(contentViewController: hosting)
        window.title = "About Valenius"
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        window.center()
        aboutWindow = window
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.aboutWindow = nil }
        }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func quit() {
        IpcClient.notifyOffline()
        NSApp.terminate(nil)
    }

    // MARK: MFA enrollment window

    private func showMfaEnroll() {
        if let existing = mfaEnrollWindow {
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        popover.performClose(nil)
        let hosting = NSHostingController(rootView: MfaEnrollView(state: state, onDone: { [weak self] in
            self?.closeMfaEnroll()
        }))
        let window = NSWindow(contentViewController: hosting)
        window.title = "Two-factor setup"
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        window.center()
        mfaEnrollWindow = window
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.mfaEnrollWindow = nil }
        }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func closeMfaEnroll() {
        mfaEnrollWindow?.close()
        mfaEnrollWindow = nil
    }

    // MARK: First-run backend URL setup

    private func showBackendUrl() {
        backendUrlAutoShown = true // stop the background poll from queuing more auto-shows
        if let existing = backendUrlWindow {
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        popover.performClose(nil)
        let hosting = NSHostingController(rootView: BackendUrlView(state: state, onDone: { [weak self] in
            self?.closeBackendUrl()
        }))
        let window = NSWindow(contentViewController: hosting)
        window.title = "Connect to your Valenius server"
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        window.center()
        backendUrlWindow = window
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.backendUrlWindow = nil }
        }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func closeBackendUrl() {
        backendUrlWindow?.close()
        backendUrlWindow = nil
    }
}

/// Stamped at build time (M7) — never hardcode a release number here.
let appVersion = "0.1.0"
