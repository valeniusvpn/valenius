import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif
import XCTest
import ValeniusShared

final class IpcDeadlineTests: XCTestCase {
    private func pair() throws -> [Int32] {
        var fds: [Int32] = [0, 0]
        #if canImport(Darwin)
        let kind = SOCK_STREAM
        #else
        let kind = Int32(SOCK_STREAM.rawValue)
        #endif
        guard socketpair(AF_UNIX, kind, 0, &fds) == 0 else { throw IpcProtocolError.ioError(errno) }
        return fds
    }

    func testSlowFrameExpires() throws {
        let fds = try pair()
        defer { close(fds[0]); close(fds[1]) }
        var byte: UInt8 = 10
        _ = write(fds[1], &byte, 1)
        let start = ProcessInfo.processInfo.systemUptime
        XCTAssertThrowsError(try IpcProtocol.readMessage(fd: fds[0], timeout: 0.1))
        XCTAssertLessThan(ProcessInfo.processInfo.systemUptime - start, 1)
    }

    func testCompleteFrameAndInvalidLength() throws {
        let fds = try pair()
        defer { close(fds[0]); close(fds[1]) }
        let message = Data("{}".utf8)
        try IpcProtocol.writeMessage(fd: fds[1], data: message, timeout: 0.1)
        XCTAssertEqual(try IpcProtocol.readMessage(fd: fds[0], timeout: 0.1), message)
        var invalid: Int32 = -1
        _ = write(fds[1], &invalid, 4)
        XCTAssertThrowsError(try IpcProtocol.readMessage(fd: fds[0], timeout: 0.1))
    }

    func testSlowReaderExpiresAndOversizeIsRefused() throws {
        let fds = try pair()
        defer { close(fds[0]); close(fds[1]) }
        var size: Int32 = 1024
        _ = setsockopt(fds[0], SOL_SOCKET, SO_SNDBUF, &size, socklen_t(MemoryLayout<Int32>.size))
        XCTAssertThrowsError(try IpcProtocol.writeMessage(fd: fds[0], data: Data(repeating: 42, count: 1_048_576), timeout: 0.1))
        XCTAssertThrowsError(try IpcProtocol.writeMessage(fd: fds[0], data: Data(repeating: 42, count: 1_048_577), timeout: 0.1))
    }
}
