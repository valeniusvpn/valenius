import XCTest
@testable import ValeniusDaemon

final class DeviceCredentialMigrationTests: XCTestCase {
    func testFailedSaveRetainsLegacyAndSuccessfulOfferSurvivesRestart() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("apikey")
        var state = DeviceCredentialState(value: "legacy-key")
        XCTAssertThrowsError(try state.adopt("individual-key", at: path, writer: { _, _ in throw CocoaError(.fileWriteNoPermission) }))
        XCTAssertEqual("legacy-key", state.value)
        XCTAssertFalse(FileManager.default.fileExists(atPath: path.path))
        try state.adopt("individual-key", at: path)
        XCTAssertEqual("individual-key", state.value)
        XCTAssertEqual("individual-key", try String(contentsOf: path, encoding: .utf8))
        XCTAssertEqual(0o600, (try FileManager.default.attributesOfItem(atPath: path.path)[.posixPermissions] as? NSNumber)?.intValue)
        XCTAssertThrowsError(try state.adopt("later-key", at: path, writer: { _, _ in throw CocoaError(.fileWriteNoPermission) }))
        XCTAssertEqual("individual-key", state.value)
        XCTAssertEqual("individual-key", try String(contentsOf: path, encoding: .utf8))
    }
}
