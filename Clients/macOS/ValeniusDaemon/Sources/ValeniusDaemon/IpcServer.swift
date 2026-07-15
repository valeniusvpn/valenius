// Unix-socket IPC server — mirrors Clients/Linux/daemon/ipc_server.py.
//   - Identifies callers via LOCAL_PEERCRED (macOS's SO_PEERCRED) -> uid -> username
//   - Same length-prefixed JSON framing as Windows named pipes / Linux
//   - Same command dispatch and response types
//
// Swift Concurrency's cooperative thread pool is a poor fit for blocking POSIX socket
// calls, so accept/read/write run on dedicated `Thread`s; `runBlocking` bridges each
// connection thread into the daemon's async `DaemonCore.dispatch`.

import Darwin
import Foundation
import ValeniusShared

let ipcSocketPath = "/var/run/valenius.sock"

enum IpcServerError: Error {
    case socketCreateFailed
    case bindFailed(Int32)
    case listenFailed(Int32)
}

final class IpcServer {
    private let core: DaemonCore
    private var listenFd: Int32 = -1
    private var acceptThread: Thread?
    private var running = false

    init(core: DaemonCore) {
        self.core = core
    }

    func start() throws {
        unlink(ipcSocketPath)

        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw IpcServerError.socketCreateFailed }

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = ipcSocketPath.utf8CString
        withUnsafeMutableBytes(of: &addr.sun_path) { rawPtr in
            let buf = rawPtr.bindMemory(to: CChar.self)
            for (i, byte) in pathBytes.enumerated() where i < buf.count {
                buf[i] = byte
            }
        }
        let addrLen = socklen_t(MemoryLayout<sockaddr_un>.size)
        let bindResult = withUnsafePointer(to: &addr) { ptr -> Int32 in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
                bind(fd, sockPtr, addrLen)
            }
        }
        guard bindResult == 0 else {
            close(fd)
            throw IpcServerError.bindFailed(errno)
        }

        guard listen(fd, 16) == 0 else {
            close(fd)
            throw IpcServerError.listenFailed(errno)
        }

        // World-writable: LOCAL_PEERCRED (not the file mode) authenticates the caller's
        // uid for per-user command isolation, so every local user needs connect access.
        chmod(ipcSocketPath, 0o666)

        listenFd = fd
        running = true
        log("IPC socket listening at \(ipcSocketPath)")

        let thread = Thread { [weak self] in self?.acceptLoop() }
        thread.name = "valeniusd.ipc.accept"
        thread.start()
        acceptThread = thread
    }

    func stop() {
        running = false
        if listenFd >= 0 { close(listenFd) }
        unlink(ipcSocketPath)
    }

    private func acceptLoop() {
        while running {
            let clientFd = accept(listenFd, nil, nil)
            guard clientFd >= 0 else {
                if running { logError("accept() failed: \(errno)") }
                continue
            }
            let thread = Thread { [weak self] in self?.handle(clientFd: clientFd) }
            thread.name = "valeniusd.ipc.conn"
            thread.start()
        }
    }

    private func handle(clientFd: Int32) {
        defer { close(clientFd) }

        guard let username = peerUsername(fd: clientFd) else {
            logError("Could not identify IPC peer")
            return
        }

        let response: PipeResponse
        do {
            let raw = try IpcProtocol.readMessage(fd: clientFd)
            let cmd = try JSONDecoder().decode(PipeCommand.self, from: raw)
            log("Command \(cmd.command) from \(username)")
            response = runBlocking { await self.core.dispatch(cmd, username: username) }
        } catch {
            logError("IPC error from \(username): \(error)")
            response = .fail("\(error)")
        }

        guard let data = try? JSONEncoder().encode(response) else { return }
        try? IpcProtocol.writeMessage(fd: clientFd, data: data)
    }

    /// getsockopt(LOCAL_PEERCRED) -> uid -> username. macOS's equivalent of Linux's
    /// SO_PEERCRED / struct ucred.
    private func peerUsername(fd: Int32) -> String? {
        var cred = xucred()
        var len = socklen_t(MemoryLayout<xucred>.size)
        let result = getsockopt(fd, 0 /* SOL_LOCAL */, LOCAL_PEERCRED, &cred, &len)
        guard result == 0 else { return nil }
        guard let pw = getpwuid(cred.cr_uid) else { return nil }
        return String(cString: pw.pointee.pw_name)
    }
}

/// Bridges a blocking thread into an async call, blocking the calling thread until the
/// operation completes. Acceptable here: IPC command volume is low (local, human-driven).
func runBlocking<T: Sendable>(_ operation: @escaping @Sendable () async -> T) -> T {
    let semaphore = DispatchSemaphore(value: 0)
    let box = UnsafeResultBox<T>()
    Task {
        box.value = await operation()
        semaphore.signal()
    }
    semaphore.wait()
    return box.value!
}

private final class UnsafeResultBox<T>: @unchecked Sendable {
    var value: T?
}
