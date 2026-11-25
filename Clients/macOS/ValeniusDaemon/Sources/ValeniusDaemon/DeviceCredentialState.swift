import Foundation

struct DeviceCredentialState {
    private(set) var value: String

    mutating func adopt(_ candidate: String?, at path: URL,
                        writer: (Data, URL) throws -> Void = { try AtomicCacheWriter.write($0, to: $1) }) throws {
        guard let candidate, !candidate.isEmpty, candidate != value else { return }
        try writer(Data(candidate.utf8), path)
        // The first use confirms migration on the backend; persistence must precede it.
        value = candidate
    }
}
