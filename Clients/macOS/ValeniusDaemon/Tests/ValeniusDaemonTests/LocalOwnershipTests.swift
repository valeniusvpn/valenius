import XCTest
@testable import ValeniusDaemon

final class LocalOwnershipTests: XCTestCase {
    func testOwnerIsExactAndSystemAttributionNeverGrantsUserControl() async throws {
        let state = try DaemonState(registration: CachedRegistration(), persist: { _ in })
        await state.setConnected(tunnel: "private", user: "alice")
        let alice = await state.canControlTunnel("private", username: "alice")
        let bob = await state.canControlTunnel("private", username: "bob")
        let alias = await state.canControlTunnel("private", username: "Alice")
        XCTAssertTrue(alice)
        XCTAssertFalse(bob)
        XCTAssertFalse(alias)
        await state.setConnected(tunnel: "device", user: "alice", systemOwned: true)
        let device = await state.canControlTunnel("device", username: "alice")
        XCTAssertFalse(device)
        await state.setDisconnected(tunnel: "private")
        let stopped = await state.canControlTunnel("private", username: "alice")
        XCTAssertFalse(stopped)
    }

    func testPrivilegeUsesUidRatherThanAccountName() {
        XCTAssertFalse(LocalPeerIdentity(username: "root", uid: 501).privileged)
        XCTAssertFalse(LocalPeerIdentity(username: "AutoConnect", uid: 501).privileged)
        XCTAssertTrue(LocalPeerIdentity(username: "root-alias", uid: 0).privileged)
        XCTAssertTrue(LocalPeerIdentity.lookup(uid: 0)?.privileged == true)
    }

    func testUnsupportedAccountNamesCannotAliasOtherUsersDirectories() {
        for name in ["domain\\alice", "alice@example.com", ".", "..", "", "álîce"] {
            XCTAssertFalse(LocalPeerIdentity.supportsUsername(name))
        }
        XCTAssertTrue(LocalPeerIdentity.supportsUsername("alice.example-1"))
    }
}
