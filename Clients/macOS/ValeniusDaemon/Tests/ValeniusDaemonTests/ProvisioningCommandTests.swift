import XCTest
@testable import ValeniusDaemon

final class ProvisioningCommandTests: XCTestCase {
    func testOriginStoreIsPrivateDurableAndDoesNotFallBackOnDamagedState() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let path = root.appendingPathComponent("backend.dat")
        XCTAssertEqual("https://installer.invalid", try BackendOriginStore.load(path: path, fallback: "https://installer.invalid"))
        try BackendOriginStore.save("https://original.invalid", path: path)
        XCTAssertEqual("https://original.invalid", try BackendOriginStore.load(path: path, fallback: "https://different.invalid"))
        let attributes = try FileManager.default.attributesOfItem(atPath: path.path)
        XCTAssertEqual(0o600, (attributes[.posixPermissions] as? NSNumber)?.intValue)
        try Data().write(to: path)
        XCTAssertThrowsError(try BackendOriginStore.load(path: path, fallback: "https://different.invalid"))
        try FileManager.default.removeItem(at: path)
        try FileManager.default.createSymbolicLink(atPath: path.path, withDestinationPath: root.appendingPathComponent("missing").path)
        XCTAssertThrowsError(try BackendOriginStore.load(path: path, fallback: "https://different.invalid"))
    }

    func testHelperAcceptsOnlyProvisioningCommandsAndKeepsAddressAsData() {
        XCTAssertNil(ProvisioningCommand.parse([]))
        XCTAssertNil(ProvisioningCommand.parse(["--provision-backend"]))
        XCTAssertNil(ProvisioningCommand.parse(["--provision-backend", ""]))
        XCTAssertNil(ProvisioningCommand.parse(["--provision-standalone", "extra"]))
        XCTAssertNil(ProvisioningCommand.parse(["--run", "command"]))
        XCTAssertEqual(.setStandaloneMode, ProvisioningCommand.parse(["--provision-standalone"])?.command)
        XCTAssertEqual("host; echo data", ProvisioningCommand.parse(["--provision-backend", "host; echo data"])?.backendDns)
    }
}
