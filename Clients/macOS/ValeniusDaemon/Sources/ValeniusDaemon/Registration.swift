import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

let registrationPath = "\(appSupportDir)/registration.json"

enum AtomicCacheWriter {
    static func write(_ data: Data, to destination: URL, beforeReplace: (URL) throws -> Void = { _ in }) throws {
        let parent = destination.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let temporary = parent.appendingPathComponent(".registration-\(UUID().uuidString)")
        let fd = open(temporary.path, O_WRONLY | O_CREAT | O_EXCL, mode_t(0o600))
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer {
            close(fd)
            try? FileManager.default.removeItem(at: temporary)
        }
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = writeBytes(fd, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
                offset += count
            }
        }
        guard fsync(fd) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        try beforeReplace(temporary)
        guard rename(temporary.path, destination.path) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let directory = open(parent.path, O_RDONLY)
        guard directory >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(directory) }
        guard fsync(directory) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }
    private static func writeBytes(_ fd: Int32, _ buffer: UnsafeRawPointer, _ count: Int) -> Int {
        #if canImport(Darwin)
        return Darwin.write(fd, buffer, count)
        #else
        return Glibc.write(fd, buffer, count)
        #endif
    }
}

enum RegistrationStore {
    static func load(path: URL = URL(fileURLWithPath: registrationPath),
                     writer: (Data, URL) throws -> Void = { try AtomicCacheWriter.write($0, to: $1) }) throws -> CachedRegistration {
        let data: Data
        do {
            data = try Data(contentsOf: path)
        } catch let error as NSError where error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError {
            var attributes = stat()
            // A dangling link is an existing identity path, not a fresh install.
            guard lstat(path.path, &attributes) != 0 && errno == ENOENT else { throw error }
            let registration = CachedRegistration()
            // Never use a fresh identity that could not first be persisted.
            try writer(JSONEncoder().encode(registration), path)
            return registration
        }
        let registration = try JSONDecoder().decode(CachedRegistration.self, from: data)
        guard registration.clientId != UUID(uuidString: "00000000-0000-0000-0000-000000000000") else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return registration
    }

    static func save(_ registration: CachedRegistration, path: URL = URL(fileURLWithPath: registrationPath)) throws {
        try AtomicCacheWriter.write(JSONEncoder().encode(registration), to: path)
    }
}
