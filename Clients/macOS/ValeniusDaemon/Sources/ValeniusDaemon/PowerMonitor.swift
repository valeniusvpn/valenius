// Sleep/wake monitor — fires `onWake` when the system resumes from sleep, so the daemon can
// re-verify its tunnels (a wake on a still-up wired link won't trigger NWPathMonitor, so the
// network-change reactor alone would miss it). Daemon-side equivalent of Windows'
// SystemEvents.PowerModeChanged / the concept doc's "IOKit power notifications".
//
// Uses IONotificationPortSetDispatchQueue to route notifications to a GCD queue, so it needs no
// CFRunLoop and coexists with the daemon's dispatchMain(). We must acknowledge the "can sleep"
// / "will sleep" messages promptly (IOAllowPowerChange) or the OS delays sleep ~30s.

import Foundation
import IOKit
import IOKit.pwr_mgt

// IOKit power-management message types (iokit_common_msg values from IOMessage.h): the Swift
// overlay doesn't reliably surface these as constants, so define them explicitly.
private let msgCanSystemSleep: UInt32 = 0xE000_0270
private let msgSystemWillSleep: UInt32 = 0xE000_0280
private let msgSystemHasPoweredOn: UInt32 = 0xE000_0300

final class PowerMonitor {
    fileprivate var rootPort: io_connect_t = 0
    private var notifierObject: io_object_t = 0
    private var notifyPort: IONotificationPortRef?
    fileprivate let onWake: @Sendable () -> Void
    private let queue = DispatchQueue(label: "com.stranto.valenius.power")

    init(onWake: @escaping @Sendable () -> Void) {
        self.onWake = onWake
    }

    func start() {
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        rootPort = IORegisterForSystemPower(refcon, &notifyPort, powerCallback, &notifierObject)
        guard rootPort != 0, let notifyPort else {
            logError("PowerMonitor: IORegisterForSystemPower failed")
            return
        }
        IONotificationPortSetDispatchQueue(notifyPort, queue)
        log("PowerMonitor: registered for sleep/wake")
    }

    func stop() {
        guard let notifyPort else { return }
        IODeregisterForSystemPower(&notifierObject)
        IOServiceClose(rootPort)
        IONotificationPortDestroy(notifyPort)
        self.notifyPort = nil
    }
}

private func powerCallback(refcon: UnsafeMutableRawPointer?, service: io_service_t,
                           messageType: UInt32, messageArgument: UnsafeMutableRawPointer?) {
    guard let refcon else { return }
    let monitor = Unmanaged<PowerMonitor>.fromOpaque(refcon).takeUnretainedValue()
    switch messageType {
    case msgCanSystemSleep, msgSystemWillSleep:
        // We never veto sleep — acknowledge immediately so we don't stall it. The tunnel + its
        // wireguard-go process survive sleep; we re-verify on wake below.
        IOAllowPowerChange(monitor.rootPort, Int(bitPattern: messageArgument))
    case msgSystemHasPoweredOn:
        monitor.onWake()
    default:
        break
    }
}
