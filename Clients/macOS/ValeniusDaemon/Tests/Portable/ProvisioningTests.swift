import Foundation
import XCTest
import ValeniusShared
@testable import ValeniusDaemon

final class CoreProvisioningTests: XCTestCase {
    func fixture() async throws -> (DaemonCore, BackendClient, DaemonState) {
        let state = try DaemonState(registration: CachedRegistration(), persist: { _ in })
        let backend = BackendClient()
        return (DaemonCore(backend: backend, state: state, configs: ConfigManager(), engine: TunnelEngine(),
            autoConnect: AutoConnectConfig(), updater: Updater()), backend, state)
    }

    func testOrdinaryCallerCannotEnrollOnFirstRunOrChooseStandalone() async throws {
        let (core, backend, state) = try await fixture()
        await backend.configure(false)
        require(!(await core.cmdSetBackendUrl(dns: "attacker.invalid").success))
        require(!(await core.cmdSetStandaloneMode().success))
        require(!(await backend.isConfigured))
        let snapshot = await state.registrationSnapshot()
        XCTAssertFalse(snapshot.standaloneModeChosen)
    }

    func testForgedJsonPrivilegeCannotChangeBackend() async throws {
        let (core, backend, _) = try await fixture()
        let original = await backend.baseUrl
        for command in [13, 14] {
            let data = Data("{\"Command\":\(command),\"BackendDns\":\"attacker.invalid\",\"Uid\":0,\"Privileged\":true}".utf8)
            let parsed = try JSONDecoder().decode(PipeCommand.self, from: data)
            require(!(await core.dispatch(parsed, username: "root").success))
        }
        let after = await backend.baseUrl
        XCTAssertEqual(original, after)
    }

    func testEvenPrivilegedCallerCannotReassignOrConvertManagedClientToStandalone() async throws {
        let (core, backend, _) = try await fixture()
        let original = await backend.baseUrl
        require(!(await core.cmdSetBackendUrl(dns: "attacker.invalid", privileged: true).success))
        require(!(await core.cmdSetStandaloneMode(privileged: true).success))
        let after = await backend.baseUrl
        XCTAssertEqual(original, after)
    }

    func testAdministratorCanProvisionInitialBackend() async throws {
        let (core, backend, _) = try await fixture()
        await backend.configure(false)
        require(await core.cmdSetBackendUrl(dns: "original.invalid", privileged: true).success)
        let after = await backend.baseUrl
        XCTAssertEqual("https://original.invalid", after)
        require(!(await core.cmdSetBackendUrl(dns: "attacker.invalid", privileged: true).success))
    }

    func testAdministratorCanSelectStandaloneOnlyBeforeEnrollment() async throws {
        let (core, backend, state) = try await fixture()
        await backend.configure(false)
        require(await core.cmdSetStandaloneMode(privileged: true).success)
        let snapshot = await state.registrationSnapshot()
        XCTAssertTrue(snapshot.standaloneModeChosen)
    }

    func testConcurrentProvisioningDoesNotTransferTrustToSecondOrigin() async throws {
        let (core, backend, _) = try await fixture()
        await backend.configure(false)
        async let first = core.cmdSetBackendUrl(dns: "first.invalid", privileged: true)
        async let second = core.cmdSetBackendUrl(dns: "second.invalid", privileged: true)
        let results = await [first, second]
        XCTAssertEqual(1, results.filter(\.success).count)
    }
}
