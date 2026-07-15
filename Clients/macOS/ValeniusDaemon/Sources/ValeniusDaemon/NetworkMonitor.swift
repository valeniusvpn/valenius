// Network-change trigger — the macOS analogue of Linux's NetworkMonitor (NM D-Bus) and
// Windows' NetworkChange.NetworkAddressChanged. Uses NWPathMonitor. Like the other clients it
// is a *dumb trigger*: it fires the callback on every transition; DaemonCore decides what to do
// (re-verify active tunnels always, apply auto-connect policy only when enabled).
//
// A 3 s debounce collapses the burst of path updates a single Wi-Fi/cable/dock change emits
// (mirrors the Windows 3 s debounce).

import Foundation
import Network

final class NetworkMonitor {
    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "com.stranto.valenius.netmon")
    private let onChange: @Sendable () -> Void
    private var debounceWork: DispatchWorkItem?
    private var started = false

    init(onChange: @escaping @Sendable () -> Void) {
        self.onChange = onChange
    }

    func start() {
        monitor.pathUpdateHandler = { [weak self] _ in self?.scheduleFire() }
        monitor.start(queue: queue)
    }

    func stop() {
        monitor.cancel()
    }

    private func scheduleFire() {
        // Ignore the very first path update (delivered immediately on start) so we don't run a
        // re-verify before anything is even connected.
        if !started { started = true; return }
        debounceWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.onChange() }
        debounceWork = work
        queue.asyncAfter(deadline: .now() + 3, execute: work)
    }
}
