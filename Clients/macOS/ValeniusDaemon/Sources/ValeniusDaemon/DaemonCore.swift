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

/// Post-connect verification retry cadence (mirrors Windows TunnelVerifier): probe roughly
/// every 5s; if nothing succeeds within ~60s, mark VerificationFailed but keep retrying at a
/// slower cadence for as long as the tunnel stays connected, rather than giving up outright.
private let verifyFastInterval: TimeInterval = 5
private let verifyFastWindow: TimeInterval = 60
private let verifySlowInterval: TimeInterval = 20

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
    /// Per-target auto-connect action cooldown (mirrors Windows AutoConnectService's
    /// `_lastActionByTarget`) — tracked independently so one target's action never blocks the
    /// other's in the same check. Keys: "AutoConnect", "DeviceTunnel".
    private var lastAutoActionByTarget: [String: Date] = [:]

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
        status.backendUrl = await backend.baseUrl
        status.backendUnconfigured = !(await backend.isConfigured)
        status.standaloneMode = await backend.getStandaloneModeChosen()
        let serverProfile = await state.getServerProfileName()
        status.availableProfiles = await configs.orderProfiles(configs.profiles(for: username), serverProfileName: serverProfile)
        status.deletableProfiles = await configs.deletableProfiles(for: username)
        status.hasStagedConfig = await configs.hasStagedConfig()
        status.autoConnectEnabled = await autoConnect.isEnabled()
        return status
    }

    // MARK: Commands

    /// First-run: the user (app setup dialog) provides the backend DNS; the daemon prepends
    /// https://, persists it, and kicks off registration immediately instead of waiting for the
    /// next heartbeat cycle. Mirrors Windows ClientRegistrationService.SetBackendUrlAsync.
    func cmdSetBackendUrl(dns: String?) async -> PipeResponse {
        guard await backend.setBaseUrl(dns) != nil else {
            return .fail("Enter a valid server address, for example vpn.example.com")
        }
        Task { await heartbeatOnce() }
        return .ok()
    }

    /// First-run: the user's setup-choice dialog explicitly opts out of ever configuring a
    /// backend — local `.conf` profiles only (upload/connect/disconnect/delete all work,
    /// nothing is ever contacted except, optionally, auto-update via `CloudUpdateSource`). No
    /// payload; persists the choice and stops the app from prompting again. Mirrors Windows
    /// RegistrationManager.SetStandaloneModeAsync / SetupChoiceForm's "Use standalone".
    func cmdSetStandaloneMode() async -> PipeResponse {
        await backend.setStandaloneModeChosen(true)
        RegistrationStore.save(
            clientId: await state.clientId, isActive: await state.isRegistrationActive(),
            standaloneModeChosen: true
        )
        return .ok()
    }

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
        // No backend configured (standalone, or mid first-run setup) — skip authorization
        // entirely, there is nothing to authorize against. Mirrors Windows: PipeServer's
        // Connect handler does the same when `!_backendUrl.IsConfigured`.
        if await backend.isConfigured, !(await state.isAllowedToConnect()) {
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

        // Local-LAN conflict check — mirrors Windows FindLocalLanConflict / Linux
        // find_local_lan_conflict. No override: the user must resolve the conflict first.
        // MUST scope remoteLanCidrs to this specific tunnelName — applying another
        // profile's (e.g. the native server's) reported LAN here would misattribute a
        // conflict to an unrelated foreign profile (a real bug found in testing).
        let remoteLanCidrs = await state.getRemoteLanCidrs(profile: tunnelName)
        if let lanConflict = await configs.findLocalLanConflict(newProfileUser: username, newProfile: tunnelName, remoteLanCidrs: remoteLanCidrs) {
            return .fail(lanConflict, isLanConflict: true)
        }

        // Decrypt in memory (never to disk) and bring the tunnel up via wireguard-go/UAPI.
        let content: String
        do {
            content = try await configs.decryptedContent(username: username, profile: tunnelName)
        } catch {
            return .fail("\(error)")
        }
        let wanIp = await backend.getWanIp()
        do {
            _ = try engine.up(profile: tunnelName, config: content)
        } catch {
            return .fail("Failed to start tunnel: \(error)")
        }

        await state.setConnected(tunnel: tunnelName, user: username)
        let clientId = await state.clientId
        Task { await backend.logEvent(clientId: clientId, eventType: "Connect", username: username, tunnelName: tunnelName, lanIp: getLanIp(), wanIp: wanIp) }
        Task { await verifyAfterConnect(tunnel: tunnelName) }
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
        // The run marker's iface — without it, engine.down skips teardownNetwork entirely and
        // the scoped scutil DNS entry (State:/Network/Service/valenius-<iface>/DNS) is orphaned.
        engine.down(profile: tunnel, iface: engine.iface(for: tunnel), searchDomains: searchDomains)
    }

    /// Post-connect verification (mirrors Linux _verify_after_connect / Windows TunnelVerifier):
    /// a sidecar profile with a gateway target probes /health through the tunnel; anything else
    /// (or a failed probe) falls back to a UAPI handshake-age check. `SkipConnectivityCheck`
    /// bypasses the probe and marks verified immediately.
    ///
    /// Recoverable, not one-shot: probes roughly every 5s; if nothing succeeds within ~60s the
    /// tunnel is marked `VerificationFailed` (app shows an amber "Confirmation failed" tag) but
    /// the loop keeps retrying at a slower 20s cadence for as long as the tunnel stays
    /// connected — a later success still clears the flag and promotes to "Verified". Also owns
    /// the port-443 fallback retry (docs/design/port-443-fallback.md): once the profile's
    /// admin-configured trigger window passes with no successful verification and a confirmed
    /// fallback port is advertised, switches to it once and keeps polling on the same cadence.
    private func verifyAfterConnect(tunnel: String) async {
        if await state.getSkipConnectivityCheck() {
            await state.setVerified(tunnel: tunnel, viaGateway: false)
            return
        }

        let start = Date()
        var failedMarked = false
        var fallbackTried = false

        while await state.isConnected(tunnel: tunnel) {
            let (ok, viaGateway) = await attemptVerify(tunnel: tunnel)
            if ok {
                await state.setVerified(tunnel: tunnel, viaGateway: viaGateway)
                if viaGateway {
                    // Mirrors Linux/Windows reporting a "Verified" event upstream — required so
                    // the admin client list's "connected" star (which now demands actual gateway
                    // verification for sidecar clients, not just Connect) ever lights up for
                    // macOS. Deliberately only this branch reports; the handshake fallback does
                    // not (handshake freshness isn't a reliable enough signal to gate a display
                    // indicator on).
                    let clientId = await state.clientId
                    Task { await backend.logEvent(clientId: clientId, eventType: "Verified", username: "", tunnelName: tunnel) }
                }
                return
            }

            if !fallbackTried, let fb = await state.getFallbackConfig(profile: tunnel),
               Date().timeIntervalSince(start) >= TimeInterval(fb.triggerSeconds) {
                fallbackTried = true
                await trySwitchToFallbackPort(tunnel: tunnel, port: fb.port, triggerSeconds: fb.triggerSeconds)
            }

            if !failedMarked, Date().timeIntervalSince(start) >= verifyFastWindow {
                failedMarked = true
                await state.markVerificationFailed(tunnel: tunnel)
                log("Confirmation failed for tunnel '\(tunnel)' after \(Int(verifyFastWindow))s — still retrying in the background.")
            }

            try? await Task.sleep(nanoseconds: UInt64(failedMarked ? verifySlowInterval : verifyFastInterval) * 1_000_000_000)
        }
    }

    /// Single verification attempt: a gateway target (if this profile has one) probes /health
    /// once; anything else, or a failed probe, falls back to a single UAPI handshake-age check.
    /// Returns (ok, viaGateway).
    private func attemptVerify(tunnel: String) async -> (ok: Bool, viaGateway: Bool) {
        if let gateway = await state.gatewayProbe(profile: tunnel),
           await Verifier.probeGatewayHealth(ip: gateway.ip, port: gateway.port) {
            return (true, true)
        }
        return (checkHandshakeFresh(tunnel: tunnel), false)
    }

    private func checkHandshakeFresh(tunnel: String) -> Bool {
        guard let iface = engine.iface(for: tunnel),
              let epoch = UapiClient.latestHandshakeEpoch(iface: iface), epoch > 0 else { return false }
        return Int(Date().timeIntervalSince1970) - epoch < 180
    }

    /// Reinstalls `tunnel` against its confirmed UDP fallback port after `triggerSeconds` passed
    /// with no successful verification. Runs at most once per connect (guarded by the caller's
    /// `fallbackTried` flag) — a later reconnect always starts back on the primary port. Unlike
    /// Windows, macOS needs no "wait for the old tunnel service to disappear" step:
    /// `TunnelEngine` always spawns a fresh utun on `up`, so there's no fixed-name reinstall
    /// conflict to race. On success, records the switch (app tag) and reports it to the backend
    /// (best-effort). On failure the tunnel is put back on its primary port — see
    /// `restorePrimaryPort` — so a failed switch can never cost the client the tunnel it
    /// already had. Mirrors Windows TunnelVerifier.TrySwitchToFallbackPortAsync.
    private func trySwitchToFallbackPort(tunnel: String, port: Int, triggerSeconds: Int) async {
        guard let content = try? await configs.decryptedContentByTunnel(tunnel) else { return }
        log("No successful verification for tunnel '\(tunnel)' after \(triggerSeconds)s — retrying via UDP fallback port \(port).")

        await tunnelDown(tunnel)
        let switched = rewriteEndpointPort(content, port: port)
        do {
            _ = try engine.up(profile: tunnel, config: switched)
            await state.markUsedFallbackPort(tunnel: tunnel, port: port)
            let clientId = await state.clientId
            Task { await backend.logEvent(clientId: clientId, eventType: "PortFallback", username: "", tunnelName: tunnel) }
        } catch {
            logError("Fallback-port reconnect failed for tunnel '\(tunnel)': \(error)")
            await restorePrimaryPort(tunnel: tunnel, content: content)
        }
    }

    /// Puts `tunnel` back on its primary port after a failed fallback-port switch. If even that
    /// fails, the state is cleared and a Disconnect is reported — a phantom "connected" is worse
    /// than an honest disconnect, because the app then only offers Disconnect and the user can
    /// never get back to a working tunnel on their own. Mirrors Windows
    /// TunnelVerifier.RestorePrimaryPortAsync.
    private func restorePrimaryPort(tunnel: String, content: String) async {
        do {
            _ = try engine.up(profile: tunnel, config: content)
            log("Restored tunnel '\(tunnel)' on its primary port after the fallback-port attempt failed.")
        } catch {
            logError("Could not restore tunnel '\(tunnel)' on its primary port: \(error)")
            await state.setDisconnected(tunnel: tunnel)
            let clientId = await state.clientId
            Task { await backend.logEvent(clientId: clientId, eventType: "Disconnect", username: "ConnectionLost", tunnelName: tunnel) }
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

    /// Apply the trusted-network auto-connect/disconnect policy to each independently-evaluated
    /// target (mirrors Windows AutoConnectService.CheckAsync / GetTargets): the generic
    /// admin-configured profile, and — when the backend's device-tunnel flag is set — the
    /// customer's own native sidecar profile. Both share the same trusted-network detection;
    /// neither ever tears down a manually-connected foreign tunnel.
    private func autoConnectAction() async {
        let networks = await autoConnect.getTrustedNetworks()
        guard !networks.isEmpty else { return }
        let inTrusted = TrustedNetworkDetector.isInTrustedNetwork(networks)

        if await autoConnect.isEnabled() {
            await runGenericAutoConnect(inTrusted: inTrusted)
        }
        if await state.getDeviceTunnelEnabled(), let deviceProfile = await state.getServerProfileName() {
            await runDeviceTunnelAutoConnect(profile: deviceProfile, inTrusted: inTrusted)
        }
    }

    /// The original admin-configured auto-connect target: an explicit profile, or (if none is
    /// configured) the server profile, or (if that's unknown too) "any connected tunnel" /
    /// "the first available profile" — unchanged from before the device-tunnel target existed.
    /// The cooldown only advances on a confirmed action, never a failed attempt, so the startup
    /// retry loop (`startupAutoConnectCheck`) can actually retry rather than self-block.
    private func runGenericAutoConnect(inTrusted: Bool) async {
        let label = "AutoConnect"
        guard Date().timeIntervalSince(lastAutoActionByTarget[label] ?? .distantPast) >= autoActionCooldown else { return }

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
            // Prefer the actual logged-in macOS user (kept fresh by every IPC call — the app
            // polls Status every 5s) over the "AutoConnect" trigger label — the label isn't
            // useful on the admin client list's "Last user" column, and it also isn't a real
            // per-user config directory, so cmdConnect couldn't find the profile without this.
            let connectedBy = await state.getLastKnownUsername() ?? label
            if await cmdConnect(username: connectedBy, profileName: serverProfile).success {
                lastAutoActionByTarget[label] = Date()
            }
        } else if isServerUp && inTrusted {
            log("Auto-connect: inside trusted network, disconnecting")
            if await cmdDisconnect(username: label, profileName: serverProfile).success {
                lastAutoActionByTarget[label] = Date()
            }
        }
    }

    /// The Windows-parity "device tunnel": the customer's own native sidecar profile
    /// (`ServerProfileName`), connected automatically before any user ever logs in, gated by
    /// the backend's `DeviceTunnelEnabled` flag rather than the user-facing AutoConnect toggle.
    /// Shares the generic target's trusted-network detection above, tracked with its own
    /// cooldown so one target's action never blocks the other's in the same check. Mirrors
    /// Windows AutoConnectService's "DeviceTunnel" AutoConnectTarget.
    private func runDeviceTunnelAutoConnect(profile: String, inTrusted: Bool) async {
        let label = "DeviceTunnel"
        guard Date().timeIntervalSince(lastAutoActionByTarget[label] ?? .distantPast) >= autoActionCooldown else { return }

        let isUp = await state.isConnected(tunnel: profile)
        if !isUp && !inTrusted {
            log("Device tunnel: outside trusted network, connecting '\(profile)'")
            if await connectDeviceTunnel(profile: profile) {
                lastAutoActionByTarget[label] = Date()
            } else {
                log("Device tunnel: no stored config for '\(profile)' yet — skipping.")
            }
        } else if isUp && inTrusted {
            log("Device tunnel: inside trusted network, disconnecting '\(profile)'")
            if await cmdDisconnect(username: label, profileName: profile).success {
                lastAutoActionByTarget[label] = Date()
            }
        }
    }

    /// Connects the device-tunnel target directly (mirrors Windows
    /// AutoConnectService.AutoConnectAsync for the DeviceTunnel target): unlike the generic
    /// auto-connect target above (which reuses `cmdConnect`, gated to a specific OS user's
    /// config directory), this must work even on a machine nobody has ever logged into, so it
    /// resolves the config cross-user first (a per-user copy may already exist from a prior
    /// login / cross-user delivery) and falls back to the machine-level device-tunnel store
    /// kept current by `ensureDeviceTunnelConfig`. Skips the AllowedIPs/LAN-conflict checks
    /// `cmdConnect` runs for interactive callers — mirrors Windows, where this path (like
    /// PendingConnect) is backend/policy-driven, not subject to the interactive guard.
    private func connectDeviceTunnel(profile: String) async -> Bool {
        if await state.isConnected(tunnel: profile) { return true }

        let content: String
        if let crossUser = try? await configs.decryptedContentByTunnel(profile) {
            content = crossUser
        } else if let machine = try? await configs.decryptedDeviceTunnelContent(profile: profile) {
            content = machine
        } else {
            return false
        }

        let wanIp = await backend.getWanIp()
        do {
            _ = try engine.up(profile: profile, config: content)
        } catch {
            logError("Device tunnel: failed to start tunnel '\(profile)': \(error)")
            return false
        }

        let connectedBy = await state.getLastKnownUsername() ?? "DeviceTunnel"
        await state.setConnected(tunnel: profile, user: connectedBy)
        let clientId = await state.clientId
        Task { await backend.logEvent(clientId: clientId, eventType: "Connect", username: connectedBy, tunnelName: profile, lanIp: getLanIp(), wanIp: wanIp) }
        Task { await verifyAfterConnect(tunnel: profile) }
        return true
    }

    /// Keeps the machine-level device-tunnel copy of `profile` current by pulling the same
    /// authoritative list the admin's per-client "profiles" view uses, and picking out the
    /// matching entry. Called from every heartbeat/poll while the device tunnel is enabled,
    /// regardless of whether any OS user is logged in — this is what lets
    /// `runDeviceTunnelAutoConnect` connect it at boot on a machine that's never been locally
    /// logged into. Best-effort: does nothing on any failure, leaving the machine copy as it
    /// was. Mirrors Windows ClientRegistrationService.EnsureDeviceTunnelConfigAsync.
    private func ensureDeviceTunnelConfig(profile: String) async {
        let clientId = await state.clientId
        guard let entries = await backend.getProfiles(clientId: clientId),
              let match = entries.first(where: { $0.fileName.caseInsensitiveCompare(profile) == .orderedSame })
        else { return }
        try? await configs.saveDeviceTunnelConfig(profile: profile, content: match.content)
    }

    // Retry window for the startup check only (mirrors Windows RunStartupCheckAsync). A network
    // stack that hasn't fully settled yet (DHCP lease, default route, DNS) right after a boot or
    // an update-triggered daemon restart can make a single attempt fail transiently — without a
    // retry here, the only remaining trigger is a genuine network-change event (which won't fire
    // if the network hasn't actually changed) or the next heartbeat/poll's admin-pushed action,
    // so an enabled target could sit disconnected for a long time after every restart.
    private let autoConnectStartupRetryInterval: TimeInterval = 10
    private let autoConnectStartupAttempts = 6 // ~1 minute total, then fall into the normal reactors.

    /// Retries the auto-connect check for up to ~1 minute right after the daemon starts. Called
    /// once from `main.swift` as its own Task, in parallel with the heartbeat/poll/update loops.
    func startupAutoConnectCheck() async {
        try? await Task.sleep(nanoseconds: 15 * 1_000_000_000)
        for attempt in 1...autoConnectStartupAttempts {
            await autoConnectAction()
            guard await isAutoConnectPending() else { return }
            if attempt < autoConnectStartupAttempts {
                try? await Task.sleep(nanoseconds: UInt64(autoConnectStartupRetryInterval) * 1_000_000_000)
            }
        }
    }

    /// True if `autoConnectAction` still has something to do for either target — used only to
    /// decide whether the startup retry loop above should keep going. Mirrors Windows
    /// IsAutoConnectPending.
    private func isAutoConnectPending() async -> Bool {
        let networks = await autoConnect.getTrustedNetworks()
        guard !networks.isEmpty, !TrustedNetworkDetector.isInTrustedNetwork(networks) else { return false }

        if await autoConnect.isEnabled() {
            var serverProfile = await autoConnect.getProfileName()
            if serverProfile == nil { serverProfile = await state.getServerProfileName() }
            let isUp: Bool
            if let serverProfile { isUp = await state.isConnected(tunnel: serverProfile) }
            else { isUp = await state.isConnected() }
            if !isUp { return true }
        }
        if await state.getDeviceTunnelEnabled(), let deviceProfile = await state.getServerProfileName(),
           !(await state.isConnected(tunnel: deviceProfile)) {
            return true
        }
        return false
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
        guard await backend.isConfigured else { return }
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
        let serverProfileName = j.string("ServerProfileName")
        await state.setServerProfileName(serverProfileName)
        await backend.setChannel(j.string("UpdateChannel"))
        await backend.setApiKey(j.string("ClientApiKey"))
        await state.setSkipConnectivityCheck(j.bool("SkipConnectivityCheck"))

        // Device tunnel (docs/design → root CLAUDE.md "Device tunnel — connect before login"):
        // gated on the backend flag AND a native profile actually existing. Refresh the
        // machine-level config copy on every heartbeat/poll, independent of any login, so the
        // startup auto-connect check can find it even on a machine nobody's logged into yet.
        let deviceTunnelEnabled = j.bool("DeviceTunnelEnabled") && serverProfileName != nil
        await state.setDeviceTunnelEnabled(deviceTunnelEnabled)
        if deviceTunnelEnabled, let serverProfileName {
            Task { await ensureDeviceTunnelConfig(profile: serverProfileName) }
        }

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

        // Per-profile remote LAN CIDR(s) behind each profile's own sidecar, for the
        // pre-connect LAN-conflict check (covers the full-tunnel/0.0.0.0/0 case). Stays
        // scoped per profile — see DaemonState.setRemoteLanCidrs for why.
        let lanCidrEntries: [(profileName: String, cidrs: [String])] =
            (j.array("RemoteLanCidrsByProfile") ?? []).compactMap { raw in
                guard let dict = raw as? [String: Any] else { return nil }
                let e = JsonObject(dict)
                guard let name = e.string("ProfileName"), !name.isEmpty else { return nil }
                let cidrs = (e.array("Cidrs") ?? []).compactMap { $0 as? String }
                return cidrs.isEmpty ? nil : (name, cidrs)
            }
        await state.setRemoteLanCidrs(lanCidrEntries)

        // Per-profile UDP fallback port + trigger window (docs/design/port-443-fallback.md),
        // consumed by verifyAfterConnect. Present only once the serving sidecar has confirmed
        // the redirect is actually active — see DaemonState.setFallbackPorts.
        let fallbackEntries: [(profileName: String, port: Int, triggerSeconds: Int)] =
            (j.array("FallbackEndpoints") ?? []).compactMap { raw in
                guard let dict = raw as? [String: Any] else { return nil }
                let e = JsonObject(dict)
                guard let name = e.string("ProfileName"), !name.isEmpty, let port = e.int("Port") else { return nil }
                return (name, port, e.int("TriggerSeconds") ?? 20)
            }
        await state.setFallbackPorts(fallbackEntries)

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
            // Same last-known-username preference as auto-connect above — "PendingConnect" is
            // meaningless on the admin client list and isn't a real per-user config directory.
            Task {
                let connectedBy = await state.getLastKnownUsername() ?? "PendingConnect"
                _ = await cmdConnect(username: connectedBy, profileName: pendingConnect)
            }
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
            guard await backend.isConfigured else {
                try? await Task.sleep(nanoseconds: 3 * 1_000_000_000)
                continue
            }
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
            guard await backend.isConfigured else {
                try? await Task.sleep(nanoseconds: 3 * 1_000_000_000)
                continue
            }
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
        await state.setLastKnownUsername(username)
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

        case .setBackendUrl:
            return await cmdSetBackendUrl(dns: cmd.backendDns)

        case .setStandaloneMode:
            return await cmdSetStandaloneMode()
        }
    }

    // MARK: Private helpers

    private func liveRegister(username: String = "") async -> (Bool?, String?) {
        guard await backend.isConfigured else { return (nil, "No backend server configured yet.") }
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
