// Length-prefixed JSON framing — identical to the Windows named-pipe / Linux Unix-socket
// protocol, so the same backend contract works for all three clients.
//
// Wire format:
//   4 bytes  signed int32, little-endian  – payload byte count
//   N bytes  UTF-8 JSON                   – message body
//
// Operates on a raw POSIX file descriptor so both the daemon (accept()ed connections)
// and the app (a connect()ed socket) can share it.

#if canImport(Darwin)
import Darwin
#endif
import Foundation

public enum IpcProtocolError: Error, Sendable {
    case connectionClosed
    case invalidLength(Int32)
    case ioError(Int32)
}

public let ipcMaxMessageBytes: Int32 = 1_048_576

public enum IpcProtocol {
    /// Blocking read of one framed message. Intended to be called off the main/cooperative
    /// thread pool (e.g. from a dedicated `Thread`), since POSIX `recv` blocks the caller.
    public static func readMessage(fd: Int32) throws -> Data {
        let lengthData = try recvExactly(fd: fd, count: 4)
        // arm64/x86_64 are both little-endian, so the wire's little-endian int32 loads
        // directly as the host's native Int32 with no byte-swap needed.
        let length = lengthData.withUnsafeBytes { $0.load(as: Int32.self) }
        guard length > 0, length <= ipcMaxMessageBytes else {
            throw IpcProtocolError.invalidLength(length)
        }
        return try recvExactly(fd: fd, count: Int(length))
    }

    public static func writeMessage(fd: Int32, data: Data) throws {
        var length = Int32(data.count)
        let header = Data(bytes: &length, count: 4)
        try sendAll(fd: fd, data: header + data)
    }

    private static func recvExactly(fd: Int32, count: Int) throws -> Data {
        var buffer = Data()
        buffer.reserveCapacity(count)
        var remaining = count
        var chunk = [UInt8](repeating: 0, count: min(count, 65536))
        while remaining > 0 {
            let toRead = min(remaining, chunk.count)
            let n = chunk.withUnsafeMutableBytes { ptr in
                recv(fd, ptr.baseAddress, toRead, 0)
            }
            if n == 0 { throw IpcProtocolError.connectionClosed }
            if n < 0 { throw IpcProtocolError.ioError(errno) }
            buffer.append(contentsOf: chunk[0..<n])
            remaining -= n
        }
        return buffer
    }

    private static func sendAll(fd: Int32, data: Data) throws {
        try data.withUnsafeBytes { (ptr: UnsafeRawBufferPointer) in
            var offset = 0
            while offset < ptr.count {
                let n = send(fd, ptr.baseAddress!.advanced(by: offset), ptr.count - offset, 0)
                if n < 0 { throw IpcProtocolError.ioError(errno) }
                offset += n
            }
        }
    }
}
