// The kernel supplies uid. Neither JSON nor a username grants root authority.
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

struct LocalPeerIdentity: Sendable {
    let username: String
    let uid: uid_t
    var privileged: Bool { uid == 0 }

    static func supportsUsername(_ name: String) -> Bool {
        let allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-"
        return !name.isEmpty && name != "." && name != ".." && name.allSatisfy { allowed.contains($0) }
    }

    static func lookup(uid: uid_t) -> LocalPeerIdentity? {
        // Connection threads must not share getpwuid's static buffer.
        var entry = passwd()
        var result: UnsafeMutablePointer<passwd>?
        var buffer = [CChar](repeating: 0, count: 16_384)
        return buffer.withUnsafeMutableBufferPointer { bytes in
            guard getpwuid_r(uid, &entry, bytes.baseAddress!, bytes.count, &result) == 0,
                  result != nil, let name = entry.pw_name else { return nil }
            let username = String(cString: name)
            guard supportsUsername(username) else { return nil }
            return LocalPeerIdentity(username: username, uid: uid)
        }
    }
}
