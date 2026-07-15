// Daemon business logic — mirrors Clients/Linux/daemon/core.py. M1 wires registration,
// heartbeat/poll, and IPC Status/Register/SyncStatus/NotifyOffline. Commands that need
// TunnelEngine/ConfigManager/AutoConnect/Updater (Connect, Disconnect, UploadConfig,
// GetConfigInfo, DeleteProfile, SetAutoConnect, CheckUpdate, MfaEnrollConfirm, SendLogs)
// are wired in the milestones that build those subsystems.

import Foundation
import ValeniusShared

/// Port from a ServerHealthUrl, defaulting to 8443 (mirrors the Windows/Linux clients).
private func healthPort(from urlString: String?) -> Int {
    guard let urlString, let url = URL(string: urlString), let port = url.port else { return 8443 }
    return port
}

/// Case-insensitive access to a decoded JSON object. The backend (ASP.NET Core) serializes
/// responses as **camelCase** (`isActive`, `heartbeatIntervalMinutes`), so a PascalCase-only
/// lookup silently reads every field as nil and defaults it to false — exactly the trap the
/// Linux client avoids by checking both `IsActive` and `isActive`. Look up by lowercased key.
struct JsonObject {
    private let byLowerKey: [String: Any]
    init(_ raw: [String: Any]) {
        var m = [String: Any](minimumCapacity: raw.count)
        for (k, v) in raw { m[k.lowercased()] = v }
        byLowerKey = m
    }
    func bool(_ key: String, _ fallback: Bool = false) -> Bool { byLowerKey[key.lowercased()] as? Bool ?? fallback }
    func int(_ key: String) -> Int? { byLowerKey[key.lowercased()] as? Int }
    func string(_ key: String) -> String? { byLowerKey[key.lowercased()] as? String }
    func array(_ key: String) -> [Any]? { byLowerKey[key.lowercased()] as? [Any] }
}

/// After a network change, let the path settle before probing, then re-verify each active
/// tunnel for up to the budget (mirrors Linux _REVERIFY_SETTLE_S / _REVERIFY_BUDGET_S).
private let reverifySettleSeconds: TimeInterval = 5
private let verifyBudgetSeconds: TimeInterval = 15
/// Minimum gap between auto-connect actions, to prevent flapping (mirrors Windows/Linux).
private let autoActionCooldown: TimeInterval = 60

actor DaemonCore {
    let backend: BackendClient
    let state: DaemonState
    let configs: ConfigManager
    let engine: TunnelEngine
    let autoConnect: AutoConnectConfig
    let updater: Updater

    private var netMonitor: NetworkMonitor?
    private var powerMonitor: PowerMonitor?
    private var handlingNetworkChange = false
    private var lastAutoAction = Date.distantPast

    init(backend: BackendClient, state: DaemonState, configs: ConfigManager, engine: TunnelEngine, autoConnect: AutoConnectConfig, updater: Updater) {
        self.backend = backend
        self.state = state
        self.configs = configs
        self.engine = engine
        self.autoConnect = autoConnect
        self.updater = updater
    }

    /// Start reacting to network changes (called once at startup, after reconciliation).
    func startNetworkMonitor() {
        let monitor = NetworkMonitor(onChange: { [weak self] in
            Task { await self?.handleNetworkChange() }
        })
        monitor.start()
        netMonitor = monitor
    }

    /// Start reacting to sleep/wake. On wake we run the same re-verify as a network change — a
    /// tunnel the machine can no longer reach (roamed networks, VPN endpoint moved) is dropped
    /// instead of showing a stale "connected"/"verified".
    func startPowerMonitor() {
        let monitor = PowerMonitor(onWake: { [weak self] in
            Task { await self?.handleWake() }
        })
        monitor.start()
        powerMonitor = monitor
    }

    private func handleWake() async {
        log("System woke from sleep — re-verifying tunnels")
        await handleNetworkChange()
    }

    /// Adopt wireguard-go tunnels that outlived a daemon restart (mirrors Linux
    /// reconcile_active_tunnels). Owner 'Unknown' → disconnectable by anyone.
    func reconcileActiveTunnels() async {
        for (profile, _) in engine.reconcile() {
            await state.setConnected(tunnel: profile, user: "Unknown")
        }
    }

    // MARK: Status

    func buildTunnelStatus(username: String) async -> TunnelStatus {
        var status = await state.buildStatusSnapshot(username: username)
        status.backendUrl = backend.baseUrl
        let serverProfile = await state.getServerProfileName()
        status.availableProfiles = await configs.orderProfiles(configs.profiles(for: username), serverProfileName: serverProfile)
        status.deletableProfiles = await configs.deletableProfiles(for: username)
        status.hasStagedConfig = await configs.hasStagedConfig()
        status.autoConnectEnabled = await autoConnect.isEnabled()
        return status
    }

    // MARK: Commands

    func cmdRegister(username: String) async -> RegistrationResult {
        let (ok, _) = await liveRegister(username: username)
        let message: String
        switch ok {
        case true: message = "VPN access is active."
        case false: message = "Registration submitted. Contact your administrator to activate this machine."
        case .none: message = "Could not reach the registration server. Please try again later."
        }
        return RegistrationResult(isActive: ok ?? false, message: message)
    }

    // MARK: Tunnel commands

    func cmdUploadConfig(username: String, profileName: String?, content: String?) async -> PipeResponse {
        guard let content, !content.isEmpty else { return .fail("Config content is required.") }
        guard ProfileNameHelper.isValid(profileName), let profileName else {
            return .fail("Profile name may only contain letters, digits, _ and - (max 50 chars).")
        }
        if let err = ConfigValidation.validateContent(content) { return .fail(err) }
        do {
            try await configs.saveConfig(username: username, profile: profileName, content: content)
            return .ok()
        } catch {
            return .fail("\(error)")
        }
    }

    func cmdGetConfigInfo(username: String) async -> PipeResponse {
        let staged = await configs.stagedProfileNames()
        await configs.claimStagedConfig(username: username)
        await reconnectIfReplaced(username: username, stagedNames: staged)
        let profiles = await configs.profiles(for: username)
        let info = ConfigInfo(
            hasConfig: !profiles.isEmpty,
            tunnelName: profiles.first,
            fileName: profiles.first.map { "\($0).conf" },
            profiles: profiles
        )
        return .ok(info)
    }

    func cmdConnect(username: String, profileName: String?) async -> PipeResponse {
        if !(await state.isAllowedToConnect()) {
            let (ok, error) = await liveRegister()
            if ok != true {
                let cid = await state.clientId
                return .fail(error ?? "Not authorized — activate key \(cid) in the admin panel.")
            }
        }
        await configs.claimStagedConfig(username: username)

        let tunnelName: String
        if let profileName {
            guard await configs.profiles(for: username).contains(profileName) else {
                return .fail("Profile '\(profileName)' not found.")
            }
            tunnelName = profileName
        } else {
            guard let first = await configs.profiles(for: username).first else {
                return .fail("No config found. Please upload a WireGuard .conf file first.")
            }
            tunnelName = first
        }

        if await state.isConnected(tunnel: tunnelName) { return .ok() }

        // AllowedIPs conflict check — mirrors Windows FindAllowedIpsConflict.
        let activeNames = await state.allConnected().map(\.name)
        if !activeNames.isEmpty,
           let conflict = await configs.findAllowedIpsConflict(newProfileUser: username, newProfile: tunnelName, activeTunnels: activeNames) {
            return .fail("AllowedIPs in '\(tunnelName)' overlap with the active tunnel '\(conflict)'. Disconnect '\(conflict)' first.")
        }

        // Decrypt in memory (never to disk) and bring the tunnel up via wireguard-go/UAPI.
        let content: String
        do {
            content = try await configs.decryptedContent(username: username, profile: tunnelName)
        } catch {
            return .fail("\(error)")
        }
        let wanIp = await backend.getWanIp()
        let handle: TunnelHandle
        do {
            handle = try engine.up(profile: tunnelName, config: content)
        } catch {
            return .fail("Failed to start tunnel: \(error)")
        }

        await state.setConnected(tunnel: tunnelName, user: username)
        let clientId = await state.clientId
        let iface = handle.iface
        Task { await backend.logEvent(clientId: clientId, eventType: "Connect", username: username, tunnelName: tunnelName, lanIp: getLanIp(), wanIp: wanIp) }
        Task { await verifyAfterConnect(tunnel: tunnelName, iface: iface) }
        return .ok()
    }

    func cmdDisconnect(username: String, profileName: String?) async -> PipeResponse {
        if let profileName {
            guard await state.isConnected(tunnel: profileName) else {
                return .fail("Tunnel '\(profileName)' is not active.")
            }
            await tunnelDown(profileName)
            await state.setDisconnected(tunnel: profileName)
            let clientId = await state.clientId
            Task { await backend.logEvent(clientId: clientId, eventType: "Disconnect", username: username, tunnelName: profileName) }
            return .ok()
        }

        // No profile specified — disconnect the primary/server tunnel.
        guard let primary = await state.primaryTunnel() else {
            return .fail("No tunnel is currently active.")
        }
        let owner = primary.entry.user
        let systemTunnel = ["AutoConnect", "PendingConnect", "Unknown"].contains(owner)
        if !systemTunnel, owner.lowercased() != username.lowercased() {
            return .fail("Not authorized to disconnect another user's tunnel.")
        }
        await tunnelDown(primary.name)
        await state.setDisconnected(tunnel: primary.name)
        let clientId = await state.clientId
        Task { await backend.logEvent(clientId: clientId, eventType: "Disconnect", username: owner, tunnelName: primary.name) }
        return .ok()
    }

    func cmdDeleteProfile(username: String, profileName: String?) async -> PipeResponse {
        guard ProfileNameHelper.isValid(profileName), let profileName else {
            return .fail("Invalid profile name.")
        }
        if await configs.isManaged(username, profileName) {
            return .fail("'\(profileName)' is managed by the administrator.")
        }
        if await state.isConnected(tunnel: profileName) {
            await tunnelDown(profileName)
            await state.setDisconnected(tunnel: profileName)
            let clientId = await state.clientId
            Task { await backend.logEvent(clientId: clientId, eventType: "Disconnect", username: username, tunnelName: profileName) }
        }
        if !(await configs.deleteUserProfile(username: username, profile: profileName)) {
            return .fail("Profile '\(profileName)' not found.")
        }
        return .ok()
    }

    /// Confirm TOTP enrollment (mirrors Linux cmd_mfa_enroll_confirm). On success, kick a
    /// heartbeat so the cleared enrollment/gate state reaches the app promptly.
    func cmdMfaEnrollConfirm(username: String, code: String) async -> PipeResponse {
        let clientId = await state.clientId
        let ok = await backend.confirmMfaEnrollment(clientId: clientId, code: code)
        if ok {
            Task { await heartbeatOnce(username: username) }
            return .ok()
        }
        return .fail("That code was not accepted. Please try again.")
    }

    /// Bring a tunnel down. Unlike Linux's `_wg_down`, macOS needs NO config file for teardown
    /// (killing wireguard-go removes the utun), so this just needs the iface + search domains
    /// for scutil cleanup, both recoverable from the run marker inside the engine.
    private func tunnelDown(_ tunnel: String) async {
        // Re-derive search domains from the stored config if we can (for scutil removal);
        // best-effort — teardown proceeds regardless.
        var searchDomains: [String] = []
        if let content = try? await configs.decryptedContentByTunnel(tunnel),
           let cfg = try? WireGuardConfig.parse(content) {
            searchDomains = cfg.searchDomains
        }
        engine.down(profile: tunnel, iface: nil, searchDomains: searchDomains)
    }

    /// Post-connect verification (mirrors Linux _verify_after_connect / Windows RunPhase2Async):
    /// a sidecar profile with a gateway target probes /health through the tunnel; anything else
    /// (or a failed probe) falls back to a UAPI handshake-age check. `SkipConnectivityCheck`
    /// bypasses the probe and marks verified immediately.
    private func verifyAfterConnect(tunnel: String, iface: String) async {
        if await state.getSkipConnectivityCheck() {
            await state.setVerified(tunnel: tunnel, viaGateway: false)
            return
        }
        if let gateway = await state.gatewayProbe(profile: tunnel) {
            let deadline = Date().addingTimeInterval(verifyBudgetSeconds)
            if await Verifier.probeGatewayUntil(ip: gateway.ip, port: gateway.port, deadline: deadline) {
                await state.setVerified(tunnel: tunnel, viaGateway: true)
                return
            }
        }
        for _ in 0..<5 {
            if !(await state.isConnected(tunnel: tunnel)) { return }
            if let epoch = UapiClient.latestHandshakeEpoch(iface: iface), epoch > 0 {
                let age = Int(Date().timeIntervalSince1970) - epoch
                if age < 180 {
                    await state.setVerified(tunnel: tunnel, viaGateway: false)
                    return
                }
            }
            try? await Task.sleep(nanoseconds: 2 * 1_000_000_000)
        }
    }

    // MARK: Network-change reactor (M4)

    /// Re-verify active tunnels (always), then apply the auto-connect policy. The settle delay
    /// lets the new path come up (DHCP, default route, WireGuard endpoint roam) before probing.
    /// A guard collapses overlapping events. Mirrors Linux _handle_network_change.
    private func handleNetworkChange() async {
        if handlingNetworkChange { return }
        handlingNetworkChange = true
        defer { handlingNetworkChange = false }
        try? await Task.sleep(nanoseconds: UInt64(reverifySettleSeconds) * 1_000_000_000)
        await reverifyActiveTunnels()
        await autoConnectAction()
    }

    /// Re-confirm each active tunnel after a network change; tear down ONLY the ones we can be
    /// confident are dead. The trusted "dead" signal is a tunnel that was gateway-verified
    /// end-to-end and whose /health probe now fails for the whole budget. Tunnels without a
    /// gateway target (foreign/plain configs, firewalled health) are left connected — a stale
    /// handshake can't distinguish "dead" from "idle/roaming" (PersistentKeepalive=25 + session
    /// reuse), so tearing them down would drop working VPNs. Do NOT simplify to handshake-based
    /// teardown. Mirrors Linux _reverify_active_tunnels.
    private func reverifyActiveTunnels() async {
        let active = await state.allConnected()
        guard !active.isEmpty else { return }
        let deadline = Date().addingTimeInterval(verifyBudgetSeconds)

        for tunnel in active {
            let name = tunnel.name
            let wasViaGateway = await state.isVerifiedViaGateway(tunnel: name)
            let gateway = await state.gatewayProbe(profile: name)
            await state.setUnverified(tunnel: name)

            if let gateway {
                if await Verifier.probeGatewayUntil(ip: gateway.ip, port: gateway.port, deadline: deadline) {
                    await state.setVerified(tunnel: name, viaGateway: true)
                    continue
                }
                if wasViaGateway {
                    log("Re-verify: tunnel '\(name)' gateway unreachable after network change — disconnecting")
                    await tunnelDown(name)
                    await state.setDisconnected(tunnel: name)
                    let clientId = await state.clientId
                    let user = tunnel.connectedUser ?? "ConnectionLost"
                    Task { await backend.logEvent(clientId: clientId, eventType: "Disconnect", username: user, tunnelName: name) }
                    continue
                }
                // Gateway probe failed but was never gateway-verified (health likely firewalled) — keep it.
            }

            // Non-gateway (or firewalled-health) tunnels: best-effort handshake refresh, no teardown.
            if let iface = currentIface(for: name),
               let epoch = UapiClient.latestHandshakeEpoch(iface: iface), epoch > 0,
               Int(Date().timeIntervalSince1970) - epoch < 180 {
                await state.setVerified(tunnel: name, viaGateway: false)
            }
        }
    }

    /// Apply the trusted-network auto-connect/disconnect policy (only when enabled). Targets the
    /// server/primary tunnel; never tears down manually-connected foreign tunnels.
    private func autoConnectAction() async {
        guard await autoConnect.isEnabled() else { return }
        let networks = await autoConnect.getTrustedNetworks()
        guard !networks.isEmpty else { return }
        if Date().timeIntervalSince(lastAutoAction) < autoActionCooldown { return }

        let inTrusted = TrustedNetworkDetector.isInTrustedNetwork(networks)
        var serverProfile = await autoConnect.getProfileName()
        if serverProfile == nil { serverProfile = await state.getServerProfileName() }
        let isServerUp: Bool
        if let serverProfile {
            isServerUp = await state.isConnected(tunnel: serverProfile)
        } else {
            isServerUp = await state.isConnected()
        }

        if !isServerUp && !inTrusted {
            log("Auto-connect: outside trusted network, connecting")
            lastAutoAction = Date()
            _ = await cmdConnect(username: "AutoConnect", profileName: serverProfile)
        } else if isServerUp && inTrusted {
            log("Auto-connect: inside trusted network, disconnecting")
            lastAutoAction = Date()
            _ = await cmdDisconnect(username: "AutoConnect", profileName: serverProfile)
        }
    }

    /// The utun interface currently backing a tunnel (from the engine's run marker), for
    /// handshake reads during re-verify.
    private func currentIface(for tunnel: String) -> String? {
        engine.iface(for: tunnel)
    }

    /// If a staged config replaced one that's currently connected, reconnect it so the new
    /// config takes effect (mirrors Linux _reconnect_if_replaced).
    private func reconnectIfReplaced(username: String, stagedNames: [String]) async {
        for name in stagedNames where await state.isConnected(tunnel: name) {
            await tunnelDown(name)
            await state.setDisconnected(tunnel: name)
            _ = await cmdConnect(username: username, profileName: name)
        }
    }

    // MARK: Heartbeat

    func heartbeatOnce(username: String = "") async {
        let clientId = await state.clientId
        let hostname = ProcessInfo.processInfo.hostName
        let trayRunning = await state.isAppRunning()
        guard let resp = await backend.register(
            clientId: clientId, hostname: hostname, username: username.isEmpty ? hostname : username,
            profiles: await configs.allProfiles(), trayRunning: trayRunning
        ) else {
            await state.setBackendReachable(false)
            return
        }
        await processStatusResponse(resp)
    }

    /// Shared by heartbeatOnce and pollLoop (mirrors Windows' ProcessStatusResponseAsync /
    /// Linux's _process_status_response) so admin actions apply identically regardless of
    /// which request delivered them.
    func processStatusResponse(_ resp: [String: Any]) async {
        let j = JsonObject(resp)
        await state.setBackendReachable(true)
        let isActive = j.bool("IsActive")
        await state.updateRegistration(isActive: isActive)
        RegistrationStore.save(clientId: await state.clientId, isActive: isActive, lastCheckedUtc: Date())

        await state.setHeartbeatInterval(minutes: j.int("HeartbeatIntervalMinutes") ?? 5)
        await state.setServerProfileName(j.string("ServerProfileName"))
        await backend.setChannel(j.string("UpdateChannel"))
        await backend.setApiKey(j.string("ClientApiKey"))
        await state.setSkipConnectivityCheck(j.bool("SkipConnectivityCheck"))

        // Auto-connect policy (admin value always overwrites `enabled`; user toggle is
        // overwritten next heartbeat). Trusted networks drive the network-change reactor.
        let trustedRaw = j.array("TrustedNetworks") ?? []
        let trusted: [TrustedNetwork] = trustedRaw.compactMap { item in
            guard let d = item as? [String: Any] else { return nil }
            let e = JsonObject(d)
            guard let subnet = e.string("Subnet"), !subnet.isEmpty else { return nil }
            return TrustedNetwork(subnet: subnet, primaryDns: e.string("PrimaryDns"))
        }
        let acEnabled = j.bool("AutoConnectEnabled")
        await autoConnect.updateFromBackend(adminEnabled: acEnabled, enabled: acEnabled,
                                            profileName: j.string("AutoConnectProfileName"), trustedNetworks: trusted)
        await autoConnect.persist()

        // Gateway /health target for the native server tunnel — consumed by re-verify.
        let serverVpnIp = j.string("ServerVpnIp")
        let serverHealthUrl = j.string("ServerHealthUrl")
        await state.setServerGateway(vpnIp: serverVpnIp, healthPort: healthPort(from: serverHealthUrl))

        // MFA state — passed through to the app via buildTunnelStatus; enroll/unlock UI is M5.
        await state.setMfaState(
            required: j.bool("MfaRequired"),
            authorizeUrl: j.string("MfaAuthorizeUrl"),
            sessionExpiresAt: j.string("MfaSessionExpiresAt"),
            enrollmentOpen: j.bool("MfaEnrollmentOpen"),
            enrollmentUri: j.string("MfaEnrollmentUri"),
            approveNumber: j.int("MfaApproveNumber")
        )

        // Pending config (native sidecar): fetch + stage for the app to auto-claim.
        if j.bool("HasPendingConfig") {
            await fetchPendingConfig()
        }

        // Inline foreign configs (Pro multi-customer) — one-shot; full flow validated in M6.
        if let foreign = j.array("PendingForeignConfigs") {
            for case let entry as [String: Any] in foreign {
                let e = JsonObject(entry)
                if let filename = e.string("FileName"), let content = e.string("Content"), !filename.isEmpty, !content.isEmpty {
                    await configs.saveStagedConfig(filename: filename, content: content)
                    log("Staged foreign config: \(filename)")
                }
            }
        }

        // Backend-initiated connect/disconnect for the server/primary tunnel only.
        if let pendingConnect = j.string("PendingConnect"), !pendingConnect.isEmpty,
           !(await state.isConnected(tunnel: pendingConnect)) {
            Task { _ = await cmdConnect(username: "PendingConnect", profileName: pendingConnect) }
        }
        if let pendingDisconnect = j.string("PendingDisconnect"), !pendingDisconnect.isEmpty,
           await state.isConnected(tunnel: pendingDisconnect) {
            Task { _ = await cmdDisconnect(username: "PendingDisconnect", profileName: pendingDisconnect) }
        }

        // MFA session expired while the gated tunnel is still up: the sidecar already dropped
        // the peer, so tear down the local server tunnel to match (mirrors Linux).
        if j.bool("MfaRequired"), let serverProfile = await state.getServerProfileName(),
           await state.isConnected(tunnel: serverProfile) {
            Task { _ = await cmdDisconnect(username: "MfaSessionExpired", profileName: serverProfile) }
        }

        // Admin-initiated cross-user delete.
        if let deleteName = j.string("PendingDeleteProfileName"), !deleteName.isEmpty {
            await handlePendingDelete(deleteName)
        }

        // Admin requested a diagnostic log upload — collect + send (Valenius-only, redacted).
        if j.bool("LogUploadRequested") {
            Task { await uploadLogs(trigger: "Admin") }
        }

        // Admin pressed "Update now" — run the check + apply immediately (one-shot).
        if j.bool("ForceUpdate") {
            Task { await updater.checkAndApply() }
        }
    }

    private func fetchPendingConfig() async {
        let clientId = await state.clientId
        guard let data = await backend.getPendingConfig(clientId: clientId) else { return }
        let j = JsonObject(data)
        guard let filename = j.string("FileName"), let content = j.string("Content"),
              !filename.isEmpty, !content.isEmpty else { return }
        await configs.saveStagedConfig(filename: filename, content: content)
        log("Staged pending config: \(filename)")
    }

    /// Check for an update; if one is available, kick off the download+install and flag the app.
    /// Mirrors Linux cmd_check_update.
    func cmdCheckUpdate() async -> VersionCheckResult {
        let result = await updater.check()
        await state.setUpdateAvailable(result.available)
        if result.available {
            Task { await updater.checkAndApply() }
        }
        return VersionCheckResult(updateAvailable: result.available, currentVersion: daemonVersion, latestVersion: result.latest)
    }

    /// Hourly update poll (mirrors the Windows UpdateChecker cadence). ForceUpdate in the
    /// heartbeat triggers an immediate apply out of band.
    func updateLoop() async {
        try? await Task.sleep(nanoseconds: 60 * 1_000_000_000) // let registration settle first
        while !Task.isCancelled {
            let result = await updater.check()
            await state.setUpdateAvailable(result.available)
            if result.available { await updater.checkAndApply() }
            try? await Task.sleep(nanoseconds: 3600 * 1_000_000_000)
        }
    }

    /// Collect the (Valenius-only, redacted) diagnostic bundle and upload it. Called on an admin
    /// heartbeat request ("Admin") and by the app's "Send logs" action ("User"). Mirrors Linux.
    func uploadLogs(trigger: String) async {
        let clientId = await state.clientId
        let apiKey = await backend.currentApiKey()
        let gz = Diagnostics.collectBundle(apiKey: apiKey)
        let ok = await backend.uploadLogs(clientId: clientId, gzData: gz, trigger: trigger)
        log("Diagnostic logs uploaded (\(trigger), \(gz.count) bytes): \(ok)")
    }

    private func handlePendingDelete(_ profile: String) async {
        if await state.isConnected(tunnel: profile) {
            await tunnelDown(profile)
            await state.setDisconnected(tunnel: profile)
        }
        await configs.deleteProfile(profile)
        log("Deleted profile (admin): \(profile)")
    }

    func heartbeatLoop() async {
        try? await Task.sleep(nanoseconds: 5 * 1_000_000_000)
        while !Task.isCancelled {
            await heartbeatOnce()
            let minutes = await state.getHeartbeatInterval()
            try? await Task.sleep(nanoseconds: UInt64(minutes) * 60 * 1_000_000_000)
        }
    }

    /// Long-polls GET /api/clients/poll (mirrors Windows' RunLongPollLoopAsync / Linux's
    /// poll_loop). The backend holds each request for up to 55 s and returns immediately
    /// when an admin action changes this client's state, so activation, MFA, etc. apply
    /// within ~1 s instead of waiting for the next heartbeat cycle (up to 60 min).
    func pollLoop() async {
        try? await Task.sleep(nanoseconds: 10 * 1_000_000_000)
        while !Task.isCancelled {
            let clientId = await state.clientId
            let trayRunning = await state.isAppRunning()
            if let resp = await backend.poll(clientId: clientId, trayRunning: trayRunning) {
                await processStatusResponse(resp)
            } else {
                await state.setBackendReachable(false)
                try? await Task.sleep(nanoseconds: 15 * 1_000_000_000)
            }
        }
    }

    // MARK: IPC dispatch

    func dispatch(_ cmd: PipeCommand, username: String) async -> PipeResponse {
        await state.markAppSeen()
        switch cmd.command {
        case .status:
            return .ok(await buildTunnelStatus(username: username))

        case .register:
            return .ok(await cmdRegister(username: username))

        case .syncStatus:
            // Fire a heartbeat AND an update check in parallel (mirrors Windows SyncStatus,
            // which runs TryRegister + UpdateChecker together) so opening the popup surfaces a
            // pending update promptly instead of waiting for the hourly cycle.
            async let hb: () = heartbeatOnce(username: username)
            async let up: VersionCheckResult = cmdCheckUpdate()
            _ = await hb
            _ = await up
            return .ok(await buildTunnelStatus(username: username))

        case .notifyOffline:
            let clientId = await state.clientId
            Task { await backend.notifyOffline(clientId: clientId) }
            return .ok()

        case .uploadConfig:
            return await cmdUploadConfig(username: username, profileName: cmd.profileName, content: cmd.configContent)

        case .getConfigInfo:
            return await cmdGetConfigInfo(username: username)

        case .connect:
            return await cmdConnect(username: username, profileName: cmd.profileName)

        case .disconnect:
            return await cmdDisconnect(username: username, profileName: cmd.profileName)

        case .deleteProfile:
            return await cmdDeleteProfile(username: username, profileName: cmd.profileName)

        case .setAutoConnect:
            guard let enabled = cmd.autoConnectEnabled else {
                return .fail("AutoConnectEnabled must be specified.")
            }
            await autoConnect.setUserEnabled(enabled)
            await autoConnect.persist()
            return .ok()

        case .checkUpdate:
            return .ok(await cmdCheckUpdate())

        case .mfaEnrollConfirm:
            guard let code = cmd.mfaCode?.trimmingCharacters(in: .whitespaces), !code.isEmpty else {
                return .fail("A TOTP code is required.")
            }
            return await cmdMfaEnrollConfirm(username: username, code: code)

        case .sendLogs:
            Task { await uploadLogs(trigger: "User") }
            return .ok()
        }
    }

    // MARK: Private helpers

    private func liveRegister(username: String = "") async -> (Bool?, String?) {
        let clientId = await state.clientId
        let hostname = ProcessInfo.processInfo.hostName
        let trayRunning = await state.isAppRunning()
        guard let resp = await backend.register(
            clientId: clientId, hostname: hostname, username: username.isEmpty ? hostname : username,
            profiles: await configs.allProfiles(), trayRunning: trayRunning
        ) else {
            await state.setBackendReachable(false)
            return (nil, "Registration server unreachable.")
        }
        await state.setBackendReachable(true)
        let active = JsonObject(resp).bool("IsActive")
        await state.updateRegistration(isActive: active)
        RegistrationStore.save(clientId: clientId, isActive: active, lastCheckedUtc: Date())
        return (active, nil)
    }
}
