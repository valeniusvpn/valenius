import Foundation
import XCTest
@testable import ValeniusDaemon

final class CoreAuthorizationTests: XCTestCase {
    func core(_ clock: AuthorizationClock) async throws -> (DaemonCore, BackendClient, DaemonState, TunnelEngine) {
        let state = try DaemonState(registration: CachedRegistration(), now: clock.now, persist: { _ in })
        await state.applyBackendStatus(backendApproval())
        let backend = BackendClient(), engine = TunnelEngine()
        return (DaemonCore(backend: backend, state: state, configs: ConfigManager(), engine: engine,
                           autoConnect: AutoConnectConfig(), updater: Updater()), backend, state, engine)
    }
    func testCachedNativeConnectWhileBackendOffline() async throws {
        let (core, _, _, engine) = try await core(AuthorizationClock())
        require(await core.cmdConnect(username: "alice", profileName: "native").success)
        XCTAssertEqual(1, engine.starts)
    }
    func testExpiredLeaseDeniesWithOfflineBackend() async throws {
        let clock = AuthorizationClock(), (core, _, _, engine) = try await core(clock)
        clock.value = clock.value.addingTimeInterval(7 * 86400)
        require(!(await core.cmdConnect(username: "alice", profileName: "native").success))
        XCTAssertEqual(0, engine.starts)
    }
    func testLiveRefreshAppliesMfaBeforeAllowingNativeStart() async throws {
        let clock = AuthorizationClock(), (core, backend, _, engine) = try await core(clock)
        clock.value = clock.value.addingTimeInterval(8 * 86400)
        await backend.setResponse(backendApproval(["MfaRequired": true]))
        require(!(await core.cmdConnect(username: "alice", profileName: "native").success))
        XCTAssertEqual(0, engine.starts)
    }
    func testAllInteractiveIdentitiesRespectDeactivation() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await core.processStatusResponse(backendApproval(["IsActive": false]))
        for user in ["alice", "AutoConnect", "PendingConnect", "DeviceTunnel"] {
            require(!(await core.cmdConnect(username: user, profileName: "native").success))
        }
        require(!(await state.isAllowedToConnect()))
        XCTAssertEqual(0, engine.starts)
    }
    func testWithdrawalDuringWanLookupPreventsStart() async throws {
        let (core, backend, state, engine) = try await core(AuthorizationClock())
        await backend.setWanHook { await state.applyBackendStatus(backendApproval(["IsActive": false])) }
        require(!(await core.cmdConnect(username: "alice", profileName: "native").success))
        XCTAssertEqual(0, engine.starts)
    }
    func testExpiredMfaAllowsForeignButBlocksNative() async throws {
        let clock = AuthorizationClock(), (core, _, _, engine) = try await core(clock)
        await core.processStatusResponse(backendApproval(["MfaSessionExpiresAt": "2026-10-26T13:00:00Z"]))
        clock.value = clock.value.addingTimeInterval(3600)
        require(!(await core.cmdConnect(username: "alice", profileName: "native").success))
        require(await core.cmdConnect(username: "alice", profileName: "foreign").success)
        XCTAssertEqual(1, engine.starts)
    }
    func testStandaloneKeepsLocalConnectWithoutApproval() async throws {
        let (core, backend, state, engine) = try await core(AuthorizationClock())
        await state.applyBackendStatus(backendApproval(["IsActive": false]))
        await backend.configure(false)
        require(await core.cmdConnect(username: "alice", profileName: "native").success)
        XCTAssertEqual(1, engine.starts)
    }
    func testPendingBooleanConnectUsesNativeProfile() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await core.processStatusResponse(backendApproval(["PendingConnect": true]))
        for _ in 0..<1000 {
            if await state.isConnected(tunnel: "native") { break }
            await Task.yield()
        }
        require(await state.isConnected(tunnel: "native"))
        XCTAssertEqual(1, engine.starts)
    }
    func testConfigReplacementCannotReconnectInactiveDevice() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await state.setConnected(tunnel: "native", user: "alice")
        await core.configs.setStaged(["native"])
        await state.applyBackendStatus(backendApproval(["IsActive": false]))
        _ = await core.cmdGetConfigInfo(username: "alice")
        XCTAssertEqual(0, engine.starts)
        XCTAssertEqual(1, engine.stops)
    }

    func testDeviceTunnelRequiresActiveApproval() async throws {
        let (core, _, _, engine) = try await core(AuthorizationClock())
        await core.processStatusResponse(backendApproval(["IsActive": false, "DeviceTunnelEnabled": true,
            "TrustedNetworks": [["Subnet": "10.0.0.0/8"]]]))
        await core.autoConnectAction()
        XCTAssertEqual(0, engine.starts)
    }
    func testDeviceTunnelWorksWithCachedApproval() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await core.processStatusResponse(backendApproval(["DeviceTunnelEnabled": true,
            "TrustedNetworks": [["Subnet": "10.0.0.0/8"]]]))
        await state.setBackendReachable(false)
        await core.autoConnectAction()
        XCTAssertEqual(1, engine.starts)
    }
    func testDeviceTunnelRespectsExpiredMfa() async throws {
        let clock = AuthorizationClock(), (core, _, _, engine) = try await core(clock)
        await core.processStatusResponse(backendApproval(["DeviceTunnelEnabled": true, "MfaSessionExpiresAt": "2026-10-26T13:00:00Z",
            "TrustedNetworks": [["Subnet": "10.0.0.0/8"]]]))
        clock.value = clock.value.addingTimeInterval(3600)
        await core.autoConnectAction()
        XCTAssertEqual(0, engine.starts)
    }
    func testFallbackAndPrimaryRestoreCannotBypassDeactivation() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await state.setConnected(tunnel: "native", user: "alice")
        await state.applyBackendStatus(backendApproval(["IsActive": false]))
        await core.trySwitchToFallbackPort(tunnel: "native", port: 443, triggerSeconds: 20)
        XCTAssertEqual(0, engine.starts)
        require(!(await state.isConnected(tunnel: "native")))
    }
    func testAllowedFallbackAndPrimaryRestoreStillWork() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await state.setConnected(tunnel: "native", user: "alice")
        await core.trySwitchToFallbackPort(tunnel: "native", port: 443, triggerSeconds: 20)
        XCTAssertEqual(1, engine.starts)
        require(await state.allConnected().first?.fallbackPortUsed == 443)
        engine.startFailures = 1
        await core.trySwitchToFallbackPort(tunnel: "native", port: 443, triggerSeconds: 20)
        XCTAssertEqual(3, engine.starts)
    }
    func testWithdrawalDuringNativeStartTriggersCompensation() async throws {
        let clock = AuthorizationClock(), (core, _, state, engine) = try await core(clock)
        engine.beforeStart = { clock.value = clock.value.addingTimeInterval(7 * 86400) }
        require(!(await core.cmdConnect(username: "alice", profileName: "native").success))
        XCTAssertEqual(1, engine.starts)
        XCTAssertEqual(1, engine.stops)
        require(!(await state.isConnected(tunnel: "native")))
    }
    func testPendingBooleanDisconnectTargetsNativeProfile() async throws {
        let (core, _, state, engine) = try await core(AuthorizationClock())
        await state.setConnected(tunnel: "native", user: "alice")
        await core.processStatusResponse(backendApproval(["PendingDisconnect": true]))
        for _ in 0..<1000 {
            if !(await state.isConnected(tunnel: "native")) { break }
            await Task.yield()
        }
        require(!(await state.isConnected(tunnel: "native")))
        XCTAssertEqual(1, engine.stops)
    }
}
