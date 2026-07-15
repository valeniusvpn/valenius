// Loads daemon configuration from appsettings.json — mirrors Linux's daemon/settings.py
// and Windows' appsettings.json (Valenius:BackendUrl / Valenius:ApiKey).

import Foundation

let appSupportDir = "/Library/Application Support/Valenius"
let settingsPath = "\(appSupportDir)/appsettings.json"

struct Settings {
    var backendUrl: String
    var apiKey: String

    private struct Root: Decodable {
        var valenius: Inner

        enum CodingKeys: String, CodingKey {
            case valenius = "Valenius"
        }
    }

    private struct Inner: Decodable {
        var backendUrl: String
        var apiKey: String

        enum CodingKeys: String, CodingKey {
            case backendUrl = "BackendUrl"
            case apiKey = "ApiKey"
        }
    }

    enum LoadError: Error, CustomStringConvertible {
        case missing(String)
        case malformed(String)

        var description: String {
            switch self {
            case .missing(let path):
                return "\(path) not found. Copy appsettings.json.example there and fill in BackendUrl + ApiKey."
            case .malformed(let reason):
                return "appsettings.json is malformed: \(reason)"
            }
        }
    }

    static func load(path: String = settingsPath) throws -> Settings {
        guard FileManager.default.fileExists(atPath: path) else {
            throw LoadError.missing(path)
        }
        let data: Data
        do {
            data = try Data(contentsOf: URL(fileURLWithPath: path))
        } catch {
            throw LoadError.malformed("\(error)")
        }
        do {
            let root = try JSONDecoder().decode(Root.self, from: data)
            let backendUrl = root.valenius.backendUrl.hasSuffix("/")
                ? String(root.valenius.backendUrl.dropLast())
                : root.valenius.backendUrl
            return Settings(backendUrl: backendUrl, apiKey: root.valenius.apiKey)
        } catch {
            throw LoadError.malformed("\(error)")
        }
    }
}
