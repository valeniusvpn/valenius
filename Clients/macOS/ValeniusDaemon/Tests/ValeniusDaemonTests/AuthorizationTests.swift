import Foundation
import XCTest
import ValeniusShared
@testable import ValeniusDaemon

final class AuthorizationClock {
    var value = AuthorizationDate.parse("2026-10-26T12:00:00Z")!
    func now() -> Date { value }
}
func backendApproval(_ changes: [String: Any] = [:]) -> [String: Any] {
    var result: [String: Any] = ["IsActive": true, "ServerProfileName": "native", "ServerVpnIp": "10.0.0.1",
        "ServerHealthUrl": "https://10.0.0.1:8444/health",
        "RemoteLanCidrsByProfile": [["ProfileName": "native", "Cidrs": ["10.20.0.0/24"]]],
        "FallbackEndpoints": [["ProfileName": "native", "Port": 443, "TriggerSeconds": 30]]]
    result.merge(changes, uniquingKeysWith: { _, new in new })
    return result
}
func require(_ condition: Bool, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertTrue(condition, message, file: file, line: line)
}

final class AuthorizationTests: XCTestCase {
    func makeState(_ clock: AuthorizationClock = .init(), persist: @escaping (CachedRegistration) throws -> Void = { _ in }) throws -> DaemonState {
        try DaemonState(registration: CachedRegistration(), now: clock.now, persist: persist)
    }
    func tempDirectory() throws -> URL {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: path, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: path) }
        return path
    }

    func testSharedLeaseAndMfaContractFixtures() async throws {
        #if os(Linux)
        let url = Bundle.module.url(forResource: "offline-access-cases", withExtension: "json")!
        #else
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let url = root.appendingPathComponent("tests/offline-access-cases.json")
        #endif
        let rows = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [[String: Any]]
        XCTAssertEqual(13, rows.count)
        for row in rows {
            let clock = AuthorizationClock()
            var registration = CachedRegistration()
            registration.isActive = row["active"] as! Bool
            registration.lastCheckedUtc = row["checked"] as? String
            registration.connectionPolicy = try ConnectionPolicy.fromBackend(backendApproval([
                "MfaRequired": row["mfaRequired"] ?? false, "MfaSessionExpiresAt": row["mfaExpires"] ?? NSNull()]))
            clock.value = AuthorizationDate.parse(row["now"] as? String)!
            let state = try DaemonState(registration: registration, offlineAccessDays: row["days"] as! Int,
                                        now: clock.now, persist: { _ in })
            require(await state.isAllowedToConnect() == row["activation"] as! Bool, row["name"] as! String)
            require(await state.isProfileAllowedToConnect("native") == row["native"] as! Bool, row["name"] as! String)
            require(await state.isProfileAllowedToConnect("foreign") == row["activation"] as! Bool, row["name"] as! String)
        }
    }

    func testRestartAndStandaloneSavesNeverRenewDeadline() async throws {
        let clock = AuthorizationClock()
        var state = try makeState(clock)
        await state.applyBackendStatus(backendApproval())
        let deadline = await state.buildStatusSnapshot(username: "test").offlineAccessExpiresAt
        for day in [1, 3, 6, 7, 30] {
            clock.value = AuthorizationDate.parse("2026-10-26T12:00:00Z")!.addingTimeInterval(Double(day) * 86400)
            let snapshot = await state.registrationSnapshot()
            state = try DaemonState(registration: snapshot, now: clock.now, persist: { _ in })
            await state.setStandaloneModeChosen(true)
            require(await state.buildStatusSnapshot(username: "test").offlineAccessExpiresAt == deadline)
            require(await state.isAllowedToConnect() == (day < 7))
        }
    }
    func testOnlySuccessfulStatusRenewsApproval() async throws {
        let clock = AuthorizationClock(), state = try makeState()
        let timed = try makeState(clock)
        await timed.applyBackendStatus(backendApproval())
        clock.value = clock.value.addingTimeInterval(2 * 86400)
        await timed.setBackendReachable(false)
        require(await timed.lastChecked() == AuthorizationDate.parse("2026-10-26T12:00:00Z"))
        await timed.applyBackendStatus(backendApproval())
        require(await timed.lastChecked() == clock.value)
        require(!(await state.isAllowedToConnect()))
    }
    func testExplicitDeactivationSurvivesDiskRestore() async throws {
        let path = try tempDirectory().appendingPathComponent("registration.json")
        let clock = AuthorizationClock()
        let state = try makeState(clock, persist: { try RegistrationStore.save($0, path: path) })
        await state.applyBackendStatus(backendApproval())
        await state.applyBackendStatus(backendApproval(["IsActive": false]))
        let restored = try DaemonState(registration: RegistrationStore.load(path: path), now: clock.now, persist: { _ in })
        require(!(await restored.isAllowedToConnect()))
        require(await restored.buildStatusSnapshot(username: "test").offlineAccessExpiresAt == nil)
    }
    func testLegacyCachePreservesIdentityAndStandaloneButNeedsRefresh() async throws {
        let path = try tempDirectory().appendingPathComponent("registration.json"), id = UUID()
        let legacy: [String: Any] = ["ClientId": id.uuidString, "IsActive": true, "LastCheckedUtc": NSNull(), "StandaloneModeChosen": true]
        try JSONSerialization.data(withJSONObject: legacy).write(to: path)
        let registration = try RegistrationStore.load(path: path)
        XCTAssertEqual(id, registration.clientId)
        XCTAssertTrue(registration.standaloneModeChosen)
        let state = try DaemonState(registration: registration, persist: { _ in })
        require(!(await state.isAllowedToConnect()))
        await state.applyBackendStatus(backendApproval())
        require(await state.isAllowedToConnect())
    }
    func testMalformedOrPartialPolicyCannotBecomeNonMfaAuthorization() async throws {
        let state = try makeState()
        await state.applyBackendStatus(backendApproval(["MfaSessionExpiresAt": "invalid"]))
        require(!(await state.isAllowedToConnect()))
        await state.applyBackendStatus(backendApproval())
        let snapshot = await state.registrationSnapshot()
        let original = try JSONSerialization.jsonObject(with: JSONEncoder().encode(snapshot)) as! [String: Any]
        for policy in [["Version": 99], ["Version": 1], [:]] as [[String: Any]] {
            var bad = original
            bad["ConnectionPolicy"] = policy
            let decoded = try JSONDecoder().decode(CachedRegistration.self, from: JSONSerialization.data(withJSONObject: bad))
            XCTAssertEqual(snapshot.clientId, decoded.clientId)
            require(!decoded.activationAllowed(at: Date(), days: 7))
        }
    }
    func testInvalidTimestampPreservesIdentityButDenies() async throws {
        var record = CachedRegistration()
        record.isActive = true
        record.lastCheckedUtc = "invalid"
        record.connectionPolicy = try ConnectionPolicy.fromBackend(backendApproval())
        let state = try DaemonState(registration: record, persist: { _ in })
        require(!(await state.isAllowedToConnect()))
    }
    func testBooleanTypesAndCamelCaseResponsesAreChecked() async throws {
        let state = try makeState()
        let response = Dictionary(backendApproval().map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { _, last in last })
        await state.applyBackendStatus(response)
        require(await state.isProfileAllowedToConnect("native"))
        for invalid in [1, "true", "false", NSNull()] as [Any] {
            await state.applyBackendStatus(backendApproval(["IsActive": invalid]))
            require(!(await state.isAllowedToConnect()))
        }
        await state.applyBackendStatus(backendApproval(["MfaRequired": 0]))
        require(!(await state.isAllowedToConnect()))
    }
    func testMfaDeadlineAndMetadataSurviveRestoreWithoutSecrets() async throws {
        let clock = AuthorizationClock(), state = try makeState()
        let timed = try makeState(clock)
        await timed.applyBackendStatus(backendApproval(["DeviceTunnelEnabled": true, "MfaSessionExpiresAt": "2026-10-26T13:00:00Z",
            "MfaAuthorizeUrl": "https://example.invalid/mfa", "MfaEnrollmentUri": "otpauth://secret-marker", "MfaApproveNumber": 42]))
        let bytes = try JSONEncoder().encode(await timed.registrationSnapshot())
        require(!String(decoding: bytes, as: UTF8.self).contains("secret-marker"))
        require(!String(decoding: bytes, as: UTF8.self).contains("MfaApproveNumber"))
        let restored = try DaemonState(registration: JSONDecoder().decode(CachedRegistration.self, from: bytes), now: clock.now, persist: { _ in })
        require(await restored.getDeviceTunnelEnabled())
        require(await restored.gatewayProbe(profile: "native")?.port == 8444)
        require(await restored.getRemoteLanCidrs(profile: "native") == ["10.20.0.0/24"])
        require(await restored.getFallbackConfig(profile: "native")?.port == 443)
        require(await restored.buildStatusSnapshot(username: "test").mfaEnrollmentUri == nil)
        clock.value = clock.value.addingTimeInterval(3600)
        require(!(await restored.isProfileAllowedToConnect("native")))
        require(await restored.isProfileAllowedToConnect("foreign"))
        require(await restored.buildStatusSnapshot(username: "test").mfaRequired)
        require(!(await state.isAllowedToConnect()))
    }
    func testUnknownNativeProfileCannotBypassRequiredMfa() async throws {
        let state = try makeState()
        await state.applyBackendStatus(backendApproval(["ServerProfileName": NSNull(), "MfaRequired": true]))
        require(!(await state.isProfileAllowedToConnect("anything")))
    }
    func testPersistenceFailureKeepsRevocationInMemory() async throws {
        let state = try makeState(persist: { _ in throw CocoaError(.fileWriteNoPermission) })
        await state.applyBackendStatus(backendApproval())
        require(await state.isAllowedToConnect())
        await state.applyBackendStatus(backendApproval(["IsActive": false]))
        require(!(await state.isAllowedToConnect()))
    }
    func testAtomicWriteIsPrivateBeforeRenameAndPreservesOldFileOnFailure() throws {
        let path = try tempDirectory().appendingPathComponent("registration.json")
        try AtomicCacheWriter.write(Data("old".utf8), to: path)
        XCTAssertThrowsError(try AtomicCacheWriter.write(Data("new".utf8), to: path, beforeReplace: { temporary in
            let attributes = try FileManager.default.attributesOfItem(atPath: temporary.path)
            XCTAssertEqual(0o600, (attributes[.posixPermissions] as! NSNumber).intValue)
            throw CocoaError(.fileWriteOutOfSpace)
        }))
        XCTAssertEqual("old", try String(contentsOf: path, encoding: .utf8))
        let contents = try FileManager.default.contentsOfDirectory(atPath: path.deletingLastPathComponent().path)
        XCTAssertEqual(["registration.json"], contents)
    }
    func testNewIdentityRequiresSuccessfulPersistence() throws {
        let path = try tempDirectory().appendingPathComponent("new.json")
        XCTAssertThrowsError(try RegistrationStore.load(path: path, writer: { _, _ in throw CocoaError(.fileWriteNoPermission) }))
        XCTAssertFalse(FileManager.default.fileExists(atPath: path.path))
        let first = try RegistrationStore.load(path: path)
        XCTAssertEqual(first.clientId, try RegistrationStore.load(path: path).clientId)
    }
    func testCorruptEmptyOrUnreadableExistingIdentityIsNeverReplaced() throws {
        let root = try tempDirectory(), path = root.appendingPathComponent("registration.json")
        for data in [Data("corrupt".utf8), try JSONSerialization.data(withJSONObject: ["ClientId": "00000000-0000-0000-0000-000000000000", "IsActive": true])] {
            try data.write(to: path)
            XCTAssertThrowsError(try RegistrationStore.load(path: path))
            XCTAssertEqual(data, try Data(contentsOf: path))
        }
        try FileManager.default.removeItem(at: path)
        try FileManager.default.createDirectory(at: path, withIntermediateDirectories: true)
        XCTAssertThrowsError(try RegistrationStore.load(path: path))
        var directory: ObjCBool = false
        require(FileManager.default.fileExists(atPath: path.path, isDirectory: &directory) && directory.boolValue)
    }
    func testDanglingIdentityLinkIsNotReplacedWithNewRegistration() throws {
        let root = try tempDirectory(), path = root.appendingPathComponent("registration.json")
        let target = root.appendingPathComponent("missing.json")
        try FileManager.default.createSymbolicLink(at: path, withDestinationURL: target)
        XCTAssertThrowsError(try RegistrationStore.load(path: path))
        XCTAssertEqual(target.path, try FileManager.default.destinationOfSymbolicLink(atPath: path.path))
    }
    func testSettingsDefaultAndBounds() throws {
        let path = try tempDirectory().appendingPathComponent("appsettings.json")
        for value in [1, 7, 365] {
            try JSONSerialization.data(withJSONObject: ["Valenius": ["BackendUrl": "", "ApiKey": "test", "OfflineAccessDays": value]]).write(to: path)
            XCTAssertEqual(value, try Settings.load(path: path.path).offlineAccessDays)
        }
        for value in [0, 366, -1, true, "7", 7.5, NSNull()] as [Any] {
            try JSONSerialization.data(withJSONObject: ["Valenius": ["BackendUrl": "", "ApiKey": "test", "OfflineAccessDays": value]]).write(to: path)
            XCTAssertThrowsError(try Settings.load(path: path.path))
        }
        try JSONSerialization.data(withJSONObject: ["Valenius": ["BackendUrl": "", "ApiKey": "test"]]).write(to: path)
        XCTAssertEqual(7, try Settings.load(path: path.path).offlineAccessDays)
    }
    func testGuardDeniesWithoutStarting() async {
        var starts = 0
        do {
            try await TunnelStartGuard.run(allowed: { false }, reason: { "denied" }, start: { starts += 1 }, compensate: {})
            XCTFail("Denied start accepted")
        } catch {}
        XCTAssertEqual(0, starts)
    }
    func testGuardCompensatesForWithdrawalDuringStart() async {
        var allowed = true, stopped = false
        do {
            try await TunnelStartGuard.run(allowed: { allowed }, reason: { "denied" }, start: { allowed = false }, compensate: { stopped = true })
            XCTFail("Withdrawn start accepted")
        } catch {}
        require(stopped)
    }
    func testGuardAllowsStartAndPropagatesEngineFailure() async throws {
        var starts = 0, stops = 0
        try await TunnelStartGuard.run(allowed: { true }, reason: { "" }, start: { starts += 1 }, compensate: { stops += 1 })
        XCTAssertEqual(1, starts)
        XCTAssertEqual(0, stops)
        do {
            try await TunnelStartGuard.run(allowed: { true }, reason: { "" }, start: { throw CocoaError(.fileReadUnknown) }, compensate: { stops += 1 })
            XCTFail("Engine error swallowed")
        } catch {}
        XCTAssertEqual(0, stops)
    }
}
