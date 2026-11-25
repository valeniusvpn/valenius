// Loads daemon configuration from appsettings.json — mirrors Linux's daemon/settings.py
// and Windows' appsettings.json (Valenius:BackendUrl / Valenius:ApiKey).

import Foundation

let appSupportDir = "/Library/Application Support/Valenius"
let settingsPath = "\(appSupportDir)/appsettings.json"

struct Settings {
    var backendUrl: String
    var apiKey: String
    var offlineAccessDays: Int = 7

    private struct Root: Decodable {
        var valenius: Inner

        enum CodingKeys: String, CodingKey {
            case valenius = "Valenius"
        }
    }

    private struct Inner: Decodable {
        var backendUrl: String
        var apiKey: String
        var offlineAccessDays: Int?

        enum CodingKeys: String, CodingKey {
            case backendUrl = "BackendUrl"
            case apiKey = "ApiKey"
            case offlineAccessDays = "OfflineAccessDays"
        }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            backendUrl = try c.decode(String.self, forKey: .backendUrl)
            apiKey = try c.decode(String.self, forKey: .apiKey)
            offlineAccessDays = c.contains(.offlineAccessDays) ? try c.decode(Int.self, forKey: .offlineAccessDays) : nil
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
            let days = root.valenius.offlineAccessDays ?? 7
            guard (1...365).contains(days) else {
                throw LoadError.malformed("Valenius.OfflineAccessDays must be an integer from 1 to 365.")
            }
            let backendUrl = root.valenius.backendUrl.hasSuffix("/")
                ? String(root.valenius.backendUrl.dropLast())
                : root.valenius.backendUrl
            return Settings(backendUrl: backendUrl, apiKey: root.valenius.apiKey, offlineAccessDays: days)
        } catch {
            throw LoadError.malformed("\(error)")
        }
    }
}
