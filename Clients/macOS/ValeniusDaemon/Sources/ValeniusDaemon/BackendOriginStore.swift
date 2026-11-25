import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

enum BackendOriginStore {
    static func load(path: URL, fallback: String) throws -> String {
        do {
            let value = try String(contentsOf: path, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !value.isEmpty else { throw CocoaError(.fileReadCorruptFile) }
            return value
        } catch let error as NSError where error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError {
            var attributes = stat()
            guard lstat(path.path, &attributes) != 0 && errno == ENOENT else { throw error }
            return fallback
        }
    }

    static func save(_ origin: String, path: URL) throws {
        try AtomicCacheWriter.write(Data(origin.utf8), to: path)
    }
}
