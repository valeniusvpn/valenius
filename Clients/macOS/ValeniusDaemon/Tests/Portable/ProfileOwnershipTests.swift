import Foundation
import XCTest
import ValeniusShared
@testable import ValeniusDaemon

final class ProfileOwnershipTests: XCTestCase {
    func fixture() async throws -> (DaemonCore, DaemonState, TunnelEngine) {
        let clock = AuthorizationClock()
        let state = try DaemonState(registration: CachedRegistration(), now: clock.now, persist: { _ in })
        await state.applyBackendStatus(backendApproval())
        let configs = ConfigManager(), engine = TunnelEngine()
        await configs.setProfiles(username: "bob", names: [])
        return (DaemonCore(backend: BackendClient(), state: state, configs: configs, engine: engine,
                           autoConnect: AutoConnectConfig(), updater: Updater()), state, engine)
    }

    func testNamedAndDefaultDisconnectDenyOtherUserAndAllowOwner() async throws {
        let (core, state, engine) = try await fixture()
        await state.setConnected(tunnel: "native", user: "alice")
        for name in ["native", nil] as [String?] {
            require(!(await core.dispatch(PipeCommand(command: .disconnect, profileName: name), username: "bob").success))
        }
        XCTAssertEqual(0, engine.stops)
        require(await core.cmdDisconnect(username: "alice", profileName: "native").success)
        XCTAssertEqual(1, engine.stops)
    }

    func testConnectDoesNotSearchOtherUsersOrAdoptSameNamedActiveTunnel() async throws {
        let (core, state, engine) = try await fixture()
        require(!(await core.cmdConnect(username: "bob", profileName: "native").success))
        await core.configs.setProfiles(username: "bob", names: ["native"])
        await state.setConnected(tunnel: "native", user: "alice")
        require(!(await core.cmdConnect(username: "bob", profileName: "native").success))
        XCTAssertEqual(0, engine.starts)
    }

    func testDeleteRequiresOwnProfileAndActiveOwnershipBeforeTeardown() async throws {
        let (core, state, engine) = try await fixture()
        await state.setConnected(tunnel: "native", user: "alice")
        require(!(await core.cmdDeleteProfile(username: "bob", profileName: "native").success))
        await core.configs.setProfiles(username: "bob", names: ["native"])
        require(!(await core.cmdDeleteProfile(username: "bob", profileName: "native").success))
        XCTAssertEqual(0, engine.stops)
        let deleted = await core.configs.deletedUsers
        XCTAssertEqual([], deleted)
        require(await core.cmdDeleteProfile(username: "alice", profileName: "native").success)
        XCTAssertEqual(1, engine.stops)
    }

    func testStagedClaimCannotReconnectAnotherUsersTunnel() async throws {
        let (core, state, engine) = try await fixture()
        await state.setConnected(tunnel: "native", user: "alice")
        await core.configs.setStaged(["native"])
        _ = await core.cmdGetConfigInfo(username: "bob")
        XCTAssertEqual(0, engine.stops)
        XCTAssertEqual(0, engine.starts)
        require(await state.canControlTunnel("native", username: "alice"))
    }

    func testSystemOwnedAndUnknownTunnelsRequireExplicitPrivilegeEvenForMatchingUsername() async throws {
        let (core, state, engine) = try await fixture()
        for owner in ["AutoConnect", "PendingConnect", "DeviceTunnel", "Unknown"] {
            await state.setConnected(tunnel: "native", user: owner, systemOwned: true)
            require(!(await core.cmdDisconnect(username: owner, profileName: "native").success))
            require(await core.dispatch(PipeCommand(command: .disconnect, profileName: "native"),
                                        username: "root", privileged: true).success)
        }
        XCTAssertEqual(4, engine.stops)
    }

    func testOwnerMatchingIsExactAndForgedJsonFieldsDoNotGrantPrivilege() async throws {
        let (core, state, engine) = try await fixture()
        await state.setConnected(tunnel: "native", user: "alice")
        require(!(await core.cmdDisconnect(username: "Alice", profileName: nil).success))
        let raw = Data(#"{"Command":1,"ProfileName":"native","Username":"alice","Uid":0,"Privileged":true}"#.utf8)
        let command = try JSONDecoder().decode(PipeCommand.self, from: raw)
        require(!(await core.dispatch(command, username: "bob").success))
        XCTAssertEqual(0, engine.stops)
    }

    func testPendingDisconnectRetainsTrustedAuthority() async throws {
        let (core, state, engine) = try await fixture()
        await state.setConnected(tunnel: "native", user: "alice")
        await core.processStatusResponse(backendApproval(["PendingDisconnect": true]))
        for _ in 0..<1000 {
            if !(await state.isConnected(tunnel: "native")) { break }
            await Task.yield()
        }
        require(!(await state.isConnected(tunnel: "native")))
        XCTAssertEqual(1, engine.stops)
    }

    func testConcurrentConnectCannotReplaceOwnerAcrossAwait() async throws {
        let (core, state, engine) = try await fixture()
        await core.configs.setProfiles(username: "bob", names: ["native"])
        async let alice = core.cmdConnect(username: "alice", profileName: "native")
        async let bob = core.cmdConnect(username: "bob", profileName: "native")
        let results = await [alice, bob]
        XCTAssertEqual(1, results.filter(\.success).count)
        XCTAssertEqual(1, engine.starts)
        require(await state.canControlTunnel("native", username: results[0].success ? "alice" : "bob"))
    }

    func testFallbackUsesOriginalContentAndRetainsOwnership() async throws {
        let (core, state, engine) = try await fixture()
        require(await core.cmdConnect(username: "alice", profileName: "native").success)
        await core.trySwitchToFallbackPort(tunnel: "native", port: 443, triggerSeconds: 20)
        XCTAssertEqual(["synthetic-config-alice", "synthetic-config-alice"], engine.configs)
        require(await state.canControlTunnel("native", username: "alice"))
    }

    func testIdentityPrivilegeDependsOnUidAndLookupIsReentrant() async throws {
        XCTAssertFalse(LocalPeerIdentity(username: "root", uid: 501).privileged)
        XCTAssertTrue(LocalPeerIdentity(username: "alias", uid: 0).privileged)
        await withTaskGroup(of: Bool.self) { group in
            for _ in 0..<30 {
                group.addTask { LocalPeerIdentity.lookup(uid: 0)?.privileged == true }
            }
            for await valid in group { XCTAssertTrue(valid) }
        }
        XCTAssertNil(LocalPeerIdentity.lookup(uid: UInt32.max - 1))
    }
}
