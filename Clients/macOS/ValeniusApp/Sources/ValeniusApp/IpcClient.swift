// Synchronous IPC client — mirrors Clients/Linux/tray/ipc_client.py. Called from the main
// (UI) thread for quick calls, and off-thread for the 5 s poll. Connects to the daemon's
// Unix socket and speaks the shared length-prefixed JSON framing.

import Darwin
import Foundation
import ValeniusShared

let ipcSocketPath = "/var/run/valenius.sock"

enum IpcError: Error, CustomStringConvertible {
    case daemonNotRunning
    case timedOut
    /// isLanConflict: true when this is a pre-connect local-LAN-conflict refusal — the app
    /// shows a blocking alert for this instead of the usual inline error text.
    case failure(String, isLanConflict: Bool)
    case other(String)

    var description: String {
        switch self {
        case .daemonNotRunning: return "The Valenius background service isn't running."
        case .timedOut: return "The background service didn't respond in time."
        case .failure(let m, _): return m
        case .other(let m): return m
        }
    }
}

enum IpcClient {
    static let timeout: TimeInterval = 10

    // MARK: Commands

    static func status() throws -> TunnelStatus {
        try decode(send(PipeCommand(command: .status)))
    }

    static func syncStatus() throws -> TunnelStatus {
        try decode(send(PipeCommand(command: .syncStatus), timeout: 12))
    }

    static func connect(profile: String?) throws {
        try expectOk(send(PipeCommand(command: .connect, profileName: profile)))
    }

    static func disconnect(profile: String?) throws {
        try expectOk(send(PipeCommand(command: .disconnect, profileName: profile)))
    }

    static func uploadConfig(profile: String, content: String) throws {
        try expectOk(send(PipeCommand(command: .uploadConfig, configContent: content, profileName: profile)))
    }

    @discardableResult
    static func getConfigInfo() throws -> ConfigInfo {
        try decode(send(PipeCommand(command: .getConfigInfo)))
    }

    static func setAutoConnect(_ enabled: Bool) throws {
        try expectOk(send(PipeCommand(command: .setAutoConnect, autoConnectEnabled: enabled)))
    }

    static func deleteProfile(_ profile: String) throws {
        try expectOk(send(PipeCommand(command: .deleteProfile, profileName: profile)))
    }

    static func register() throws -> RegistrationResult {
        try decode(send(PipeCommand(command: .register)))
    }

    static func mfaEnrollConfirm(code: String) throws {
        try expectOk(send(PipeCommand(command: .mfaEnrollConfirm, mfaCode: code)))
    }

    static func sendLogs() throws {
        try expectOk(send(PipeCommand(command: .sendLogs)))
    }

    /// Best-effort: tells the daemon the app is closing so the backend shows offline promptly.
    static func notifyOffline() {
        _ = try? send(PipeCommand(command: .notifyOffline), timeout: 2)
    }

    // MARK: Transport

    private static func send(_ cmd: PipeCommand, timeout: TimeInterval = timeout) throws -> PipeResponse {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw IpcError.other("socket() failed") }
        defer { close(fd) }

        var tv = timeval(tv_sec: Int(timeout), tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = ipcSocketPath.utf8CString
        withUnsafeMutableBytes(of: &addr.sun_path) { raw in
            let buf = raw.bindMemory(to: CChar.self)
            for (i, b) in pathBytes.enumerated() where i < buf.count { buf[i] = b }
        }
        let connected = withUnsafePointer(to: &addr) { ptr -> Int32 in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard connected == 0 else {
            throw (errno == ENOENT || errno == ECONNREFUSED) ? IpcError.daemonNotRunning : IpcError.other("connect() failed (errno \(errno))")
        }

        do {
            let payload = try JSONEncoder().encode(cmd)
            try IpcProtocol.writeMessage(fd: fd, data: payload)
            let respData = try IpcProtocol.readMessage(fd: fd)
            return try JSONDecoder().decode(PipeResponse.self, from: respData)
        } catch let e as IpcProtocolError {
            switch e {
            case .connectionClosed: throw IpcError.daemonNotRunning
            case .ioError(let n) where n == EAGAIN || n == EWOULDBLOCK: throw IpcError.timedOut
            default: throw IpcError.other("\(e)")
            }
        }
    }

    private static func expectOk(_ resp: PipeResponse) throws {
        if !resp.success {
            throw IpcError.failure(resp.error ?? "The operation failed.", isLanConflict: resp.isLanConflict)
        }
    }

    private static func decode<T: Decodable>(_ resp: PipeResponse) throws -> T {
        guard resp.success else {
            throw IpcError.failure(resp.error ?? "The operation failed.", isLanConflict: resp.isLanConflict)
        }
        guard let json = resp.dataJson, let data = json.data(using: .utf8) else {
            throw IpcError.other("Empty response from the background service.")
        }
        return try JSONDecoder().decode(T.self, from: data)
    }
}
