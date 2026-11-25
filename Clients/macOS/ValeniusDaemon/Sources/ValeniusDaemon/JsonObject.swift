import Foundation
import CoreFoundation

/// Backend responses use camelCase; older responses may use PascalCase.
struct JsonObject {
    private let values: [String: Any]
    init(_ raw: [String: Any]) {
        values = Dictionary(raw.map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { _, last in last })
    }
    func value(_ key: String) -> Any? { values[key.lowercased()] }
    func bool(_ key: String, _ fallback: Bool = false) -> Bool {
        guard let n = value(key) as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() else { return fallback }
        return n.boolValue
    }
    func int(_ key: String) -> Int? {
        guard let n = value(key) as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(),
              n.doubleValue == Double(n.intValue) else { return nil }
        return n.intValue
    }
    func string(_ key: String) -> String? { value(key) as? String }
    func array(_ key: String) -> [Any]? { value(key) as? [Any] }
}
