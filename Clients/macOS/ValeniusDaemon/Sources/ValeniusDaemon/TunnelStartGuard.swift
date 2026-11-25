import Foundation

enum TunnelAuthorizationError: Error, CustomStringConvertible {
    case denied(String)
    case withdrawn
    var description: String {
        switch self {
        case .denied(let reason): return reason
        case .withdrawn: return "VPN authorization changed while connecting. Disconnect and refresh access."
        }
    }
}

/// All native starts, including device and port-recovery paths, share this gate.
enum TunnelStartGuard {
    static func run(allowed: () async -> Bool, reason: () async -> String,
                    start: () async throws -> Void, compensate: () async -> Void) async throws {
        guard await allowed() else { throw TunnelAuthorizationError.denied(await reason()) }
        try await start()
        guard await allowed() else {
            await compensate()
            throw TunnelAuthorizationError.withdrawn
        }
    }
}
