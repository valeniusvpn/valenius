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
#elseif canImport(Glibc)
import Glibc
#endif
import Foundation

public enum IpcProtocolError: Error, Sendable {
    case connectionClosed
    case timedOut
    case invalidLength(Int32)
    case ioError(Int32)
}

public let ipcMaxMessageBytes: Int32 = 1_048_576

public enum IpcProtocol {
    /// Blocking read of one framed message. Intended to be called off the main/cooperative
    /// thread pool (e.g. from a dedicated `Thread`), since POSIX `recv` blocks the caller.
    public static func readMessage(fd: Int32, timeout: TimeInterval? = nil) throws -> Data {
        let deadline = timeout.map { ProcessInfo.processInfo.systemUptime + $0 }
        let lengthData = try recvExactly(fd: fd, count: 4, deadline: deadline)
        // arm64/x86_64 are both little-endian, so the wire's little-endian int32 loads
        // directly as the host's native Int32 with no byte-swap needed.
        let length = lengthData.withUnsafeBytes { $0.loadUnaligned(as: Int32.self) }
        guard length > 0, length <= ipcMaxMessageBytes else {
            throw IpcProtocolError.invalidLength(length)
        }
        return try recvExactly(fd: fd, count: Int(length), deadline: deadline)
    }

    public static func writeMessage(fd: Int32, data: Data, timeout: TimeInterval? = nil) throws {
        guard data.count <= Int(ipcMaxMessageBytes) else { throw IpcProtocolError.invalidLength(Int32.max) }
        var length = Int32(data.count)
        let header = Data(bytes: &length, count: 4)
        try sendAll(fd: fd, data: header + data, deadline: timeout.map { ProcessInfo.processInfo.systemUptime + $0 })
    }

    private static func recvExactly(fd: Int32, count: Int, deadline: TimeInterval?) throws -> Data {
        var buffer = Data()
        buffer.reserveCapacity(count)
        var remaining = count
        var chunk = [UInt8](repeating: 0, count: min(count, 65536))
        while remaining > 0 {
            try wait(fd: fd, event: Int16(POLLIN), deadline: deadline)
            let toRead = min(remaining, chunk.count)
            let n = chunk.withUnsafeMutableBytes { ptr in
                recv(fd, ptr.baseAddress, toRead, deadline == nil ? 0 : Int32(MSG_DONTWAIT))
            }
            if n == 0 { throw IpcProtocolError.connectionClosed }
            if n < 0 {
                if errno == EINTR || (deadline != nil && (errno == EAGAIN || errno == EWOULDBLOCK)) { continue }
                throw IpcProtocolError.ioError(errno)
            }
            buffer.append(contentsOf: chunk[0..<n])
            remaining -= n
        }
        return buffer
    }

    private static func wait(fd: Int32, event: Int16, deadline: TimeInterval?) throws {
        guard let deadline else { return }
        while true {
            let remaining = deadline - ProcessInfo.processInfo.systemUptime
            guard remaining > 0 else { throw IpcProtocolError.timedOut }
            var descriptor = pollfd(fd: fd, events: event, revents: 0)
            let result = poll(&descriptor, 1, Int32(min(remaining * 1000 + 1, Double(Int32.max))))
            if result > 0 { return }
            if result == 0 { throw IpcProtocolError.timedOut }
            if errno != EINTR { throw IpcProtocolError.ioError(errno) }
        }
    }

    private static func sendAll(fd: Int32, data: Data, deadline: TimeInterval?) throws {
        #if canImport(Darwin)
        var noSignal: Int32 = 1
        _ = setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, socklen_t(MemoryLayout<Int32>.size))
        let signalFlags: Int32 = 0
        #else
        let signalFlags = Int32(MSG_NOSIGNAL)
        #endif
        try data.withUnsafeBytes { (ptr: UnsafeRawBufferPointer) in
            var offset = 0
            while offset < ptr.count {
                try wait(fd: fd, event: Int16(POLLOUT), deadline: deadline)
                let n = send(fd, ptr.baseAddress!.advanced(by: offset), ptr.count - offset,
                             signalFlags | (deadline == nil ? 0 : Int32(MSG_DONTWAIT)))
                if n < 0 {
                    if errno == EINTR || (deadline != nil && (errno == EAGAIN || errno == EWOULDBLOCK)) { continue }
                    throw IpcProtocolError.ioError(errno)
                }
                if n == 0 { throw IpcProtocolError.connectionClosed }
                offset += n
            }
        }
    }
}
