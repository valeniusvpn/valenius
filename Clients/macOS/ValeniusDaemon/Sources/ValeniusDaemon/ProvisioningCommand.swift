import Foundation
import ValeniusShared
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

enum ProvisioningCommand {
    static func parse(_ arguments: [String]) -> PipeCommand? {
        if arguments.count == 2, arguments[0] == "--provision-backend", !arguments[1].isEmpty {
            return PipeCommand(command: .setBackendUrl, backendDns: arguments[1])
        }
        if arguments == ["--provision-standalone"] { return PipeCommand(command: .setStandaloneMode) }
        return nil
    }

    static func run(_ arguments: [String], socketPath: String = "/var/run/valenius.sock") -> Int32 {
        guard geteuid() == 0 else { return fail("Provisioning requires root; ask an administrator to use sudo.") }
        guard let command = parse(arguments) else {
            return fail("Usage: valeniusd --provision-backend <server> or --provision-standalone")
        }
        #if canImport(Darwin)
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        #else
        let fd = socket(AF_UNIX, Int32(SOCK_STREAM.rawValue), 0)
        #endif
        guard fd >= 0 else { return fail("Could not create provisioning socket.") }
        defer { close(fd) }
        var deadline = timeval(tv_sec: 15, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &deadline, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &deadline, socklen_t(MemoryLayout<timeval>.size))
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let path = socketPath.utf8CString
        guard path.count <= MemoryLayout.size(ofValue: address.sun_path) else { return fail("Socket path is too long.") }
        withUnsafeMutableBytes(of: &address.sun_path) { bytes in
            for (index, value) in path.enumerated() { bytes[index] = UInt8(bitPattern: value) }
        }
        let connected = withUnsafePointer(to: &address) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        guard connected == 0 else { return fail("Cannot reach the running Valenius daemon.") }
        do {
            try IpcProtocol.writeMessage(fd: fd, data: JSONEncoder().encode(command))
            let response = try JSONDecoder().decode(PipeResponse.self, from: IpcProtocol.readMessage(fd: fd))
            guard response.success else { return fail(response.error ?? "Provisioning refused.") }
            print(response.error ?? "Provisioning saved.")
            return 0
        } catch { return fail("Provisioning failed: \(error)") }
    }

    private static func fail(_ message: String) -> Int32 {
        FileHandle.standardError.write(Data((message + "\n").utf8))
        return 1
    }
}
