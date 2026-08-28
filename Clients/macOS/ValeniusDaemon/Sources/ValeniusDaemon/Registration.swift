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
        /// True once the user explicitly chose "Use standalone" in the first-run setup
        /// dialog. Mirrors Windows RegistrationState.StandaloneModeChosen.
        var standaloneModeChosen: Bool

        enum CodingKeys: String, CodingKey {
            case clientId = "ClientId"
            case isActive = "IsActive"
            case lastCheckedUtc = "LastCheckedUtc"
            case standaloneModeChosen = "StandaloneModeChosen"
        }

        init(clientId: String, isActive: Bool, lastCheckedUtc: String?, standaloneModeChosen: Bool) {
            self.clientId = clientId
            self.isActive = isActive
            self.lastCheckedUtc = lastCheckedUtc
            self.standaloneModeChosen = standaloneModeChosen
        }

        /// A registration.json written before standaloneModeChosen existed has no such key —
        /// default it to false rather than failing the whole decode.
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            clientId = try c.decode(String.self, forKey: .clientId)
            isActive = try c.decode(Bool.self, forKey: .isActive)
            lastCheckedUtc = try c.decodeIfPresent(String.self, forKey: .lastCheckedUtc)
            standaloneModeChosen = try c.decodeIfPresent(Bool.self, forKey: .standaloneModeChosen) ?? false
        }
    }

    /// Returns the ClientId to use: read from disk if present and parseable, otherwise a
    /// freshly generated one (also true when the file is missing). Mirrors Linux's
    /// registration.load(), which re-mints on any read/parse failure rather than trying
    /// to distinguish "missing" from "unreadable" (unlike Windows).
    static func loadClientId() -> (id: UUID, wasActive: Bool, standaloneModeChosen: Bool) {
        guard FileManager.default.fileExists(atPath: registrationPath) else {
            let id = UUID()
            save(clientId: id, isActive: false, standaloneModeChosen: false)
            return (id, false, false)
        }
        do {
            let data = try Data(contentsOf: URL(fileURLWithPath: registrationPath))
            let shape = try JSONDecoder().decode(FileShape.self, from: data)
            guard let id = UUID(uuidString: shape.clientId) else { throw CocoaError(.fileReadCorruptFile) }
            return (id, shape.isActive, shape.standaloneModeChosen)
        } catch {
            log("Could not read registration.json (\(error)) — generating new client ID")
            let id = UUID()
            save(clientId: id, isActive: false, standaloneModeChosen: false)
            return (id, false, false)
        }
    }

    /// `standaloneModeChosen`: nil preserves whatever is currently persisted (the common case —
    /// every heartbeat/register save just refreshes `isActive`/`lastCheckedUtc`); pass a
    /// concrete value only when actually changing it (`DaemonCore.cmdSetStandaloneMode`).
    static func save(clientId: UUID, isActive: Bool, lastCheckedUtc: Date? = nil, standaloneModeChosen: Bool? = nil) {
        let shape = FileShape(
            clientId: clientId.uuidString,
            isActive: isActive,
            lastCheckedUtc: lastCheckedUtc.map { ISO8601DateFormatter().string(from: $0) },
            standaloneModeChosen: standaloneModeChosen ?? currentStandaloneModeChosen()
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

    private static func currentStandaloneModeChosen() -> Bool {
        guard let data = FileManager.default.contents(atPath: registrationPath),
              let shape = try? JSONDecoder().decode(FileShape.self, from: data) else { return false }
        return shape.standaloneModeChosen
    }
}
