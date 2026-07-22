// Observable view-model bridging the synchronous IpcClient to SwiftUI. All IPC runs off the
// main thread; @Published updates are hopped back to main. Mirrors the state the Linux/Windows
// trays render from a TunnelStatus.

import Combine
import Foundation
import ValeniusShared

@MainActor
final class AppState: ObservableObject {
    @Published var status: TunnelStatus = TunnelStatus()
    @Published var lastError: String?
    /// Set instead of `lastError` when a connect attempt was refused for a local-LAN conflict —
    /// the view shows a blocking alert for this instead of the usual inline error text, since it
    /// must not be missed or silently replaced by the next poll's state.
    @Published var lanConflictMessage: String?
    @Published var busyProfiles: Set<String> = []   // rows with an in-flight connect/disconnect
    @Published var daemonReachable = true

    private let ipcQueue = DispatchQueue(label: "com.stranto.valenius.ipc", qos: .userInitiated)
    private var claimInFlight = false

    // MARK: Applying a fresh status

    /// Diff the incoming status against the current one: log on change, auto-claim any staged
    /// admin-pushed config (mirrors Linux _auto_claim_config / Windows AutoClaimConfigAsync),
    /// and post a notification for newly-arrived profiles.
    private func apply(_ new: TunnelStatus) {
        let oldProfiles = Set(status.availableProfiles)
        let changed = status.availableProfiles != new.availableProfiles
            || status.connectedTunnels.map(\.name) != new.connectedTunnels.map(\.name)
            || status.registrationIsActive != new.registrationIsActive
            || status.hasStagedConfig != new.hasStagedConfig
            || status.mfaRequired != new.mfaRequired
        status = new
        if changed {
            appLog("status: active=\(new.registrationIsActive.map(String.init) ?? "nil") profiles=\(new.availableProfiles) connected=\(new.connectedTunnels.map(\.name)) staged=\(new.hasStagedConfig)")
        }

        // Auto-claim staged configs — GetConfigInfo moves them into the user dir (managed).
        if new.hasStagedConfig && !claimInFlight {
            claimInFlight = true
            ipcQueue.async { [weak self] in
                _ = try? IpcClient.getConfigInfo()
                Task { @MainActor in
                    self?.claimInFlight = false
                    self?.refresh()
                }
            }
        }

        // Notify on newly-claimed profiles (skip the very first populate).
        let arrived = Set(new.availableProfiles).subtracting(oldProfiles)
        if !arrived.isEmpty && !oldProfiles.isEmpty {
            for p in arrived { Notifications.post(title: "New VPN profile", body: "\(p) is ready to connect.") }
        }
    }

    // MARK: Polling

    /// Refresh status off the main thread. `sync` fires a heartbeat too (used on popover open).
    func refresh(sync: Bool = false) {
        ipcQueue.async { [weak self] in
            do {
                let status = sync ? try IpcClient.syncStatus() : try IpcClient.status()
                Task { @MainActor in
                    self?.apply(status)
                    self?.daemonReachable = true
                }
            } catch {
                Task { @MainActor in
                    self?.daemonReachable = false
                    if case IpcError.daemonNotRunning = error { self?.status = TunnelStatus() }
                }
            }
        }
    }

    // MARK: Actions

    func connect(profile: String) {
        run(profile: profile) { try IpcClient.connect(profile: profile) }
    }

    func disconnect(profile: String) {
        run(profile: profile) { try IpcClient.disconnect(profile: profile) }
    }

    func disconnectAll() {
        for tunnel in status.connectedTunnels {
            let name = tunnel.name
            run(profile: name) { try IpcClient.disconnect(profile: name) }
        }
    }

    func deleteProfile(_ profile: String) {
        run(profile: profile) { try IpcClient.deleteProfile(profile) }
    }

    func register() {
        ipcQueue.async { [weak self] in
            do {
                let result = try IpcClient.register()
                Task { @MainActor in
                    self?.lastError = result.isActive ? nil : result.message
                    self?.refresh()
                }
            } catch {
                Task { @MainActor in self?.lastError = "\(error)" }
            }
        }
    }

    func uploadConfig(profile: String, content: String) {
        ipcQueue.async { [weak self] in
            do {
                try IpcClient.uploadConfig(profile: profile, content: content)
                Task { @MainActor in self?.refresh() }
            } catch {
                Task { @MainActor in self?.lastError = "\(error)" }
            }
        }
    }

    func sendLogs() {
        ipcQueue.async { [weak self] in
            do { try IpcClient.sendLogs() }
            catch { Task { @MainActor in self?.lastError = "\(error)" } }
        }
    }

    /// Set the backend server address (first-run setup dialog). Returns nil on success — any
    /// non-fatal advisory (e.g. "saved, but unreachable yet") is posted as a notification
    /// rather than blocking the dialog — or an error message for the dialog to show inline so
    /// the user can retry. Mirrors Windows BackendUrlForm / TrayApplicationContext.PromptBackendUrlAsync.
    func setBackendUrl(dns: String) async -> String? {
        await withCheckedContinuation { (cont: CheckedContinuation<String?, Never>) in
            ipcQueue.async { [weak self] in
                do {
                    let resp = try IpcClient.setBackendUrl(dns: dns)
                    guard resp.success else {
                        cont.resume(returning: resp.error ?? "Could not save the server address.")
                        return
                    }
                    if let warning = resp.error, !warning.isEmpty {
                        Notifications.post(title: "Valenius", body: warning)
                    }
                    Task { @MainActor in self?.refresh() }
                    cont.resume(returning: nil)
                } catch {
                    cont.resume(returning: "\(error)")
                }
            }
        }
    }

    /// Confirm MFA enrollment with a TOTP code. Returns nil on success, else an error message
    /// for the enrollment dialog to show.
    func mfaEnrollConfirm(code: String) async -> String? {
        await withCheckedContinuation { (cont: CheckedContinuation<String?, Never>) in
            ipcQueue.async {
                do { try IpcClient.mfaEnrollConfirm(code: code); cont.resume(returning: nil) }
                catch { cont.resume(returning: "\(error)") }
            }
        }
    }

    /// Run a profile-scoped action, marking the row busy and refreshing on completion.
    private func run(profile: String, _ op: @escaping () throws -> Void) {
        busyProfiles.insert(profile)
        lastError = nil
        ipcQueue.async { [weak self] in
            var failure: String?
            var lanConflict: String?
            do { try op() }
            catch IpcError.failure(let message, let isLanConflict) {
                if isLanConflict { lanConflict = message } else { failure = message }
            }
            catch { failure = "\(error)" }
            Task { @MainActor in
                self?.busyProfiles.remove(profile)
                if let failure { self?.lastError = failure }
                if let lanConflict { self?.lanConflictMessage = lanConflict }
                self?.refresh()
            }
        }
    }
}
