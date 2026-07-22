// Minimal client for the wireguard-go UAPI (userspace API) — the same text protocol the
// Windows client reads handshakes from. Socket lives at /var/run/wireguard/<iface>.sock.
//
// Protocol: send an operation ("set=1\n…\n\n" or "get=1\n\n"), then read the response until
// a blank line; the response ends with "errno=<n>" (0 = success).

import Darwin
import Foundation

let wireguardSocketDir = "/var/run/wireguard"

enum UapiError: Error, CustomStringConvertible {
    case connectFailed(String, Int32)
    case protocolError(String)
    case operationFailed(Int32)

    var description: String {
        switch self {
        case .connectFailed(let iface, let e): return "UAPI connect to \(iface) failed (errno \(e))"
        case .protocolError(let m): return "UAPI protocol error: \(m)"
        case .operationFailed(let n): return "UAPI operation failed (errno \(n))"
        }
    }
}

enum UapiClient {
    static func socketPath(iface: String) -> String { "\(wireguardSocketDir)/\(iface).sock" }

    /// Apply a `set=1` request. Throws on a non-zero UAPI errno.
    static func applyConfig(iface: String, setRequest: String) throws {
        let response = try exchange(iface: iface, request: setRequest)
        let errno = parseErrno(response)
        if errno != 0 {
            // Temporary diagnostic: dump the exact request wireguard-go rejected, with key
            // material redacted (private_key/public_key/preshared_key values only, never
            // logged in full) so the precise line-by-line format is visible.
            let redacted = setRequest.split(separator: "\n", omittingEmptySubsequences: false).map { line -> String in
                for prefix in ["private_key=", "public_key=", "preshared_key="] {
                    if line.hasPrefix(prefix) { return "\(prefix)<redacted len=\(line.count - prefix.count)>" }
                }
                return String(line)
            }.joined(separator: "\\n")
            log("UAPI set rejected (errno \(errno)) on \(iface). Request: \(redacted) | Full response: \(response.replacingOccurrences(of: "\n", with: "\\n"))")
            throw UapiError.operationFailed(errno)
        }
    }

    /// Most recent handshake age helpers read this. Returns the raw `get=1` response text.
    static func getStatus(iface: String) throws -> String {
        try exchange(iface: iface, request: "get=1\n\n")
    }

    /// Newest `last_handshake_time_sec` across peers (0 if never). Used by M4 verification —
    /// the same UAPI field the Windows HandshakeVerifier reads.
    static func latestHandshakeEpoch(iface: String) -> Int? {
        guard let response = try? getStatus(iface: iface) else { return nil }
        var newest = 0
        for line in response.split(separator: "\n") {
            if line.hasPrefix("last_handshake_time_sec=") {
                let v = Int(line.dropFirst("last_handshake_time_sec=".count)) ?? 0
                newest = max(newest, v)
            }
        }
        return newest
    }

    private static func exchange(iface: String, request: String) throws -> String {
        let path = socketPath(iface: iface)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw UapiError.connectFailed(iface, errno) }
        defer { close(fd) }

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = path.utf8CString
        withUnsafeMutableBytes(of: &addr.sun_path) { raw in
            let buf = raw.bindMemory(to: CChar.self)
            for (i, b) in pathBytes.enumerated() where i < buf.count { buf[i] = b }
        }
        let connectResult = withUnsafePointer(to: &addr) { ptr -> Int32 in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                connect(fd, sockPtr, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard connectResult == 0 else { throw UapiError.connectFailed(iface, errno) }

        let outData = Data(request.utf8)
        try outData.withUnsafeBytes { (ptr: UnsafeRawBufferPointer) in
            var off = 0
            while off < ptr.count {
                let n = send(fd, ptr.baseAddress!.advanced(by: off), ptr.count - off, 0)
                if n <= 0 { throw UapiError.protocolError("send failed (errno \(errno))") }
                off += n
            }
        }

        // Read until the terminating blank line (\n\n).
        var received = Data()
        var chunk = [UInt8](repeating: 0, count: 4096)
        while true {
            let n = chunk.withUnsafeMutableBytes { recv(fd, $0.baseAddress, 4096, 0) }
            if n < 0 { throw UapiError.protocolError("recv failed (errno \(errno))") }
            if n == 0 { break }
            received.append(contentsOf: chunk[0..<n])
            if received.count >= 2, received.suffix(2) == Data([0x0a, 0x0a]) { break }
        }
        return String(decoding: received, as: UTF8.self)
    }

    private static func parseErrno(_ response: String) -> Int32 {
        for line in response.split(separator: "\n") where line.hasPrefix("errno=") {
            return Int32(line.dropFirst("errno=".count)) ?? -1
        }
        return -1
    }
}
