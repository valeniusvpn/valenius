// Persist and restore ClientKey/IsActive across daemon restarts — mirrors
// Clients/Linux/daemon/registration.py. Deleting registration.json means a new identity
// and requires admin re-activation.

import Foundation

let registrationPath = "\(appSupportDir)/registration.json"

enum RegistrationStore {
    private struct FileShape: Codable {
        var clientId: String
        var isActive: Bool
        var lastCheckedUtc: String?

        enum CodingKeys: String, CodingKey {
            case clientId = "ClientId"
            case isActive = "IsActive"
            case lastCheckedUtc = "LastCheckedUtc"
        }
    }

    /// Returns the ClientId to use: read from disk if present and parseable, otherwise a
    /// freshly generated one (also true when the file is missing). Mirrors Linux's
    /// registration.load(), which re-mints on any read/parse failure rather than trying
    /// to distinguish "missing" from "unreadable" (unlike Windows).
    static func loadClientId() -> (id: UUID, wasActive: Bool) {
        guard FileManager.default.fileExists(atPath: registrationPath) else {
            let id = UUID()
            save(clientId: id, isActive: false)
            return (id, false)
        }
        do {
            let data = try Data(contentsOf: URL(fileURLWithPath: registrationPath))
            let shape = try JSONDecoder().decode(FileShape.self, from: data)
            guard let id = UUID(uuidString: shape.clientId) else { throw CocoaError(.fileReadCorruptFile) }
            return (id, shape.isActive)
        } catch {
            log("Could not read registration.json (\(error)) — generating new client ID")
            let id = UUID()
            save(clientId: id, isActive: false)
            return (id, false)
        }
    }

    static func save(clientId: UUID, isActive: Bool, lastCheckedUtc: Date? = nil) {
        let shape = FileShape(
            clientId: clientId.uuidString,
            isActive: isActive,
            lastCheckedUtc: lastCheckedUtc.map { ISO8601DateFormatter().string(from: $0) }
        )
        do {
            try FileManager.default.createDirectory(
                atPath: appSupportDir, withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700]
            )
            let data = try JSONEncoder().encode(shape)
            try data.write(to: URL(fileURLWithPath: registrationPath), options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: registrationPath)
        } catch {
            log("Could not write registration.json: \(error)")
        }
    }
}
