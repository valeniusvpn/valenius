// Valenius daemon (valeniusd) — entry point for the root LaunchDaemon.
// Mirrors Clients/Linux/daemon/__main__.py.

import Darwin
import Dispatch
import Foundation

guard geteuid() == 0 else {
    FileHandle.standardError.write("valeniusd must be run as root\n".data(using: .utf8)!)
    exit(1)
}

let settings: Settings
do {
    settings = try Settings.load()
} catch {
    FileHandle.standardError.write("\(error)\n".data(using: .utf8)!)
    exit(1)
}

try? FileManager.default.createDirectory(
    atPath: appSupportDir, withIntermediateDirectories: true,
    attributes: [.posixPermissions: 0o700]
)

let (clientId, wasActive) = RegistrationStore.loadClientId()
let state = DaemonState(clientId: clientId, isActive: wasActive)

let backend = BackendClient(baseUrl: settings.backendUrl, apiKey: settings.apiKey)
let configs = ConfigManager()
let engine = TunnelEngine()
let autoConnect = AutoConnectConfig()
let updater = Updater(backend: backend)
let core = DaemonCore(backend: backend, state: state, configs: configs, engine: engine, autoConnect: autoConnect, updater: updater)

// Create dirs, migrate legacy plain configs, load auto-connect config, adopt any tunnels that
// outlived a restart, and start the network-change monitor — before the IPC server accepts
// commands, so a Connect can't race reconciliation (matches the Linux ordering). runBlocking
// keeps main.swift synchronous (no top-level await → no SIGTRAP).
runBlocking {
    await configs.bootstrap()
    await autoConnect.load()
    await core.reconcileActiveTunnels()
    await core.startNetworkMonitor()
    await core.startPowerMonitor()
}
Updater.cleanupStaleArtifacts()

let ipc = IpcServer(core: core)
do {
    try ipc.start()
} catch {
    logError("Could not start IPC server: \(error)")
    exit(1)
}

// The heartbeat/poll loops run on the concurrency runtime's own threads; dispatchMain()
// services the main queue (signal sources) and keeps the process alive.
let heartbeatTask = Task { await core.heartbeatLoop() }
let pollTask = Task { await core.pollLoop() }
let updateTask = Task { await core.updateLoop() }

log("Valenius daemon started (ClientId=\(clientId))")

// MARK: Graceful shutdown

func shutdown() {
    log("Shutting down")
    heartbeatTask.cancel()
    pollTask.cancel()
    updateTask.cancel()
    ipc.stop()
    // Best-effort: tell the backend we're going offline so the admin panel updates
    // immediately, instead of waiting for the ~7-minute presence threshold to expire.
    let group = DispatchGroup()
    group.enter()
    Task {
        await backend.notifyOffline(clientId: clientId)
        group.leave()
    }
    _ = group.wait(timeout: .now() + 3)
    log("Daemon stopped")
    exit(0)
}

signal(SIGTERM, SIG_IGN)
signal(SIGINT, SIG_IGN)
let sigtermSource = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
sigtermSource.setEventHandler { shutdown() }
sigtermSource.resume()
let sigintSource = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
sigintSource.setEventHandler { shutdown() }
sigintSource.resume()

dispatchMain()
