// Shared daemon state — mirrors Clients/Linux/daemon/state.py. An `actor` gives us the
// same thread-safety Linux gets from its `threading.Lock`, without hand-rolled locking.
//
// Tunnel bookkeeping (ConnectedTunnels, verification flags, gateway probe target) is
// scaffolded here now so M2's TunnelEngine has a home to wire into, but nothing populates
// it yet — M1 has no tunnels.

import Foundation
import ValeniusShared

private let gracePeriod: TimeInterval = 24 * 60 * 60 // 1 day
/// The app polls the daemon over IPC every 5 s (mirrors Linux's _TRAY_ACTIVE_THRESHOLD).
/// Anything heard within 3x that interval counts as "app running".
private let appActiveThreshold: TimeInterval = 15

struct TunnelEntry {
    var user: String
    var since: Date
    var verified: Bool = false
    var verifiedViaGateway: Bool = false
    /// Mirrors Windows TunnelStateManager.Entry.VerificationFailed — see `markVerificationFailed`.
    var verificationFailed: Bool = false
    /// Mirrors Windows TunnelStateManager.Entry.FallbackPortUsed — see `markUsedFallbackPort`.
    var fallbackPortUsed: Int = 0
}

actor DaemonState {
    private var active: [String: TunnelEntry] = [:]
    private(set) var clientId: UUID
    private var isActive: Bool = false
    private var lastCheckedUtc: Date?
    private var updateAvailable: Bool = false
    private var serverProfileName: String?
    private var serverVpnIp: String?
    private var serverHealthPort: Int = 8443
    /// Per-profile physical LAN CIDR(s) behind that profile's own sidecar, self-reported via
    /// /info and relayed by the backend. Refreshed on every heartbeat/poll. Used by the
    /// pre-connect LAN-conflict check to detect the remote network for full-tunnel profiles.
    /// Keyed by profile name — the native server profile and each foreign profile each
    /// report their OWN source customer's LAN; applying one profile's CIDRs to a different
    /// profile's connect would misattribute a conflict (a real bug found in testing).
    private var remoteLanCidrsByProfile: [String: [String]] = [:]
    private var skipConnectivityCheck = false
    /// Backend-resolved (Customer.DeviceTunnelDefaultEnabled / Client.DeviceTunnelEnabled
    /// override) — gates the device-tunnel auto-connect target. Mirrors Windows
    /// RegistrationManager._deviceTunnelEnabled.
    private var deviceTunnelEnabled = false
    /// Per-profile UDP fallback port + trigger window, refreshed on every heartbeat. Present
    /// only when the serving sidecar has confirmed the redirect is actually active — see
    /// docs/design/port-443-fallback.md §4.4 — so a profile absent here means "no fallback
    /// offered", never "fallback offered but unconfirmed". Mirrors Windows
    /// RegistrationManager._fallbackPorts.
    private var fallbackPortsByProfile: [String: (port: Int, triggerSeconds: Int)] = [:]
    private var heartbeatIntervalMinutes: Int = 5
    private var appLastSeen: Date?
    /// Last real macOS username seen on any IPC call (`dispatch` refreshes this on every
    /// command, including the app's 5s Status poll, so it's rarely more than a few seconds
    /// stale). Used so internal-trigger connect events (AutoConnect, PendingConnect) log who
    /// was actually using the machine instead of the trigger-source label — that label alone
    /// isn't useful on the admin client list's "Last user" column. Mirrors Windows
    /// RegistrationManager._lastKnownUsername.
    private var lastKnownUsername: String?

    // MFA state — written by processStatusResponse, read by buildTunnelStatus.
    private var mfaRequired = false
    private var mfaAuthorizeUrl: String?
    private var mfaSessionExpiresAt: String?
    private var mfaEnrollmentOpen = false
    private var mfaEnrollmentUri: String?
    private var mfaApproveNumber: Int?

    /// Nil until the first heartbeat/poll/register completes — lets the app show a
    /// neutral "Checking backend..." instead of a false "unreachable".
    private var backendReachable: Bool?

    init(clientId: UUID, isActive: Bool = false) {
        self.clientId = clientId
        self.isActive = isActive
        // Mirror Linux's registration.load(), which seeds last_checked on load so the
        // 24h connect grace applies from startup, not only after the first heartbeat.
        self.lastCheckedUtc = Date()
    }

    // MARK: Multi-tunnel state (M2 wires these)

    func setConnected(tunnel: String, user: String) {
        active[tunnel] = TunnelEntry(user: user, since: Date())
    }

    func setDisconnected(tunnel: String?) {
        if let tunnel { active.removeValue(forKey: tunnel) } else { active.removeAll() }
    }

    func setVerified(tunnel: String, viaGateway: Bool) {
        active[tunnel]?.verified = true
        active[tunnel]?.verifiedViaGateway = viaGateway
        active[tunnel]?.verificationFailed = false
    }

    func setUnverified(tunnel: String) {
        active[tunnel]?.verified = false
        active[tunnel]?.verificationFailed = false
    }

    /// Marks that the initial verification window elapsed with no success — not terminal,
    /// the caller keeps retrying at a slower cadence. Cleared by a later `setVerified`.
    /// Mirrors Windows TunnelStateManager.MarkVerificationFailed.
    func markVerificationFailed(tunnel: String) {
        active[tunnel]?.verificationFailed = true
    }

    /// Records that `tunnel` switched to its UDP fallback port (docs/design/port-443-fallback.md).
    /// Sticky for the life of the connection — only a fresh `setConnected` clears it.
    /// Mirrors Windows TunnelStateManager.MarkUsedFallbackPort.
    func markUsedFallbackPort(tunnel: String, port: Int) {
        active[tunnel]?.fallbackPortUsed = port
    }

    func isVerifiedViaGateway(tunnel: String) -> Bool {
        active[tunnel]?.verifiedViaGateway ?? false
    }

    func isConnected(tunnel: String? = nil) -> Bool {
        guard let tunnel else { return !active.isEmpty }
        return active[tunnel] != nil
    }

    func allConnected() -> [ConnectedTunnelInfo] {
        active.map { name, entry in
            ConnectedTunnelInfo(
                name: name,
                isVerified: entry.verified,
                verificationFailed: entry.verificationFailed,
                fallbackPortUsed: entry.fallbackPortUsed,
                connectedUser: entry.user,
                connectedSince: ISO8601DateFormatter().string(from: entry.since)
            )
        }
    }

    /// Backward-compat: the server profile if connected, else the given preferred name, else the first.
    func primaryTunnel(preferred: String? = nil) -> (name: String, entry: TunnelEntry)? {
        guard !active.isEmpty else { return nil }
        if let preferred, let entry = active[preferred] { return (preferred, entry) }
        if let serverProfileName, let entry = active[serverProfileName] { return (serverProfileName, entry) }
        guard let first = active.first else { return nil }
        return (first.key, first.value)
    }

    // MARK: Registration

    func setClientId(_ id: UUID) {
        clientId = id
    }

    func updateRegistration(isActive: Bool) {
        self.isActive = isActive
        self.lastCheckedUtc = Date()
    }

    func isRegistrationActive() -> Bool {
        isActive
    }

    func lastChecked() -> Date? {
        lastCheckedUtc
    }

    func isAllowedToConnect() -> Bool {
        if isActive { return true }
        if let lastCheckedUtc {
            return Date().timeIntervalSince(lastCheckedUtc) < gracePeriod
        }
        return false
    }

    // MARK: MFA

    func setMfaState(
        required: Bool,
        authorizeUrl: String?,
        sessionExpiresAt: String?,
        enrollmentOpen: Bool,
        enrollmentUri: String?,
        approveNumber: Int?
    ) {
        mfaRequired = required
        mfaAuthorizeUrl = authorizeUrl
        mfaSessionExpiresAt = sessionExpiresAt
        mfaEnrollmentOpen = enrollmentOpen
        mfaEnrollmentUri = enrollmentUri
        mfaApproveNumber = approveNumber
    }

    func setBackendReachable(_ reachable: Bool) {
        backendReachable = reachable
    }

    func isBackendReachable() -> Bool? {
        backendReachable
    }

    // MARK: App presence

    func markAppSeen() {
        appLastSeen = Date()
    }

    func isAppRunning() -> Bool {
        guard let appLastSeen else { return false }
        return Date().timeIntervalSince(appLastSeen) < appActiveThreshold
    }

    /// Mirrors Windows RegistrationManager.SetLastKnownUsername. Only `dispatch` (real IPC
    /// callers, authenticated via LOCAL_PEERCRED) calls this, so the cached value is always a
    /// genuine macOS username, never an internal trigger label. Guards against an empty string
    /// defensively, matching the Windows implementation.
    func setLastKnownUsername(_ username: String) {
        if !username.isEmpty { lastKnownUsername = username }
    }

    func getLastKnownUsername() -> String? { lastKnownUsername }

    // MARK: Update / misc

    func setUpdateAvailable(_ v: Bool) {
        updateAvailable = v
    }

    func getUpdateAvailable() -> Bool {
        updateAvailable
    }

    func setHeartbeatInterval(minutes: Int) {
        heartbeatIntervalMinutes = max(1, minutes)
    }

    func getHeartbeatInterval() -> Int {
        heartbeatIntervalMinutes
    }

    func setServerProfileName(_ name: String?) {
        serverProfileName = name
    }

    func getServerProfileName() -> String? {
        serverProfileName
    }

    func setServerGateway(vpnIp: String?, healthPort: Int) {
        serverVpnIp = (vpnIp?.isEmpty ?? true) ? nil : vpnIp
        serverHealthPort = healthPort
    }

    /// `entries`: (profileName, cidrs) pairs from RemoteLanCidrsByProfile.
    func setRemoteLanCidrs(_ entries: [(profileName: String, cidrs: [String])]) {
        var map: [String: [String]] = [:]
        for (name, cidrs) in entries where !name.isEmpty && !cidrs.isEmpty { map[name] = cidrs }
        remoteLanCidrsByProfile = map
    }

    /// The remote LAN CIDR(s) reported for `profileName`'s own sidecar, or empty when none
    /// are known for that specific profile.
    func getRemoteLanCidrs(profile profileName: String?) -> [String] {
        guard let profileName else { return [] }
        return remoteLanCidrsByProfile[profileName] ?? []
    }

    func setSkipConnectivityCheck(_ v: Bool) { skipConnectivityCheck = v }
    func getSkipConnectivityCheck() -> Bool { skipConnectivityCheck }

    func setDeviceTunnelEnabled(_ v: Bool) { deviceTunnelEnabled = v }
    func getDeviceTunnelEnabled() -> Bool { deviceTunnelEnabled }

    /// `entries`: (profileName, port, triggerSeconds) from StatusResponse.FallbackEndpoints.
    func setFallbackPorts(_ entries: [(profileName: String, port: Int, triggerSeconds: Int)]) {
        var map: [String: (port: Int, triggerSeconds: Int)] = [:]
        for (name, port, triggerSeconds) in entries where !name.isEmpty && port > 0 {
            map[name] = (port, triggerSeconds > 0 ? triggerSeconds : 20)
        }
        fallbackPortsByProfile = map
    }

    /// The UDP fallback port + trigger window (seconds of no successful verification before
    /// trying it) advertised for `profile`, or nil when none is offered (no customer opt-in,
    /// or opted-in but not confirmed active). Mirrors Windows RegistrationManager.GetFallbackConfig.
    func getFallbackConfig(profile: String) -> (port: Int, triggerSeconds: Int)? {
        fallbackPortsByProfile[profile]
    }

    /// Gateway /health target (ip, port) for the server profile, or nil. Only the native
    /// server tunnel has a gateway target; foreign profiles fall back to handshake.
    func gatewayProbe(profile: String?) -> (ip: String, port: Int)? {
        guard let profile, let serverVpnIp, profile == serverProfileName else { return nil }
        return (serverVpnIp, serverHealthPort)
    }

    func buildStatusSnapshot(username: String) -> TunnelStatus {
        var status = TunnelStatus()
        let connectedTunnels = allConnected()
        let primary = primaryTunnel()
        status.isConnected = primary != nil
        status.isVerified = connectedTunnels.first?.isVerified ?? false
        let primaryInfo = primary.flatMap { p in connectedTunnels.first { $0.name == p.name } }
        status.verificationFailed = primaryInfo?.verificationFailed ?? false
        status.fallbackPortUsed = primaryInfo?.fallbackPortUsed ?? 0
        status.tunnelName = primary?.name
        status.connectedUser = primary?.entry.user
        status.connectedSince = primary.map { ISO8601DateFormatter().string(from: $0.entry.since) }
        status.connectedTunnels = connectedTunnels
        status.registrationIsActive = isActive
        status.updateAvailable = updateAvailable
        status.mfaRequired = mfaRequired
        status.mfaAuthorizeUrl = mfaAuthorizeUrl
        status.mfaSessionExpiresAt = mfaSessionExpiresAt
        status.mfaEnrollmentOpen = mfaEnrollmentOpen
        status.mfaEnrollmentUri = mfaEnrollmentUri
        status.mfaApproveNumber = mfaApproveNumber
        status.backendReachable = backendReachable
        return status
    }
}
