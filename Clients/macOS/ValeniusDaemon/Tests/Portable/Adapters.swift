// Linux-only platform doubles. The portable runner compiles the actual DaemonCore
// against these; a native macOS build still checks the real platform adapters.
import Foundation
import ValeniusShared

func log(_ message: String) {}
func logError(_ message: String) {}
func getLanIp() -> String { "192.0.2.1" }
let daemonVersion = "portable-test"
func rewriteEndpointPort(_ content: String, port: Int) -> String { content }
struct TrustedNetwork { let subnet: String; let primaryDns: String? }
enum ConfigValidation { static func validateContent(_ content: String) -> String? { nil } }
enum TrustedNetworkDetector { static func isInTrustedNetwork(_ networks: [TrustedNetwork]) -> Bool { false } }
final class NetworkMonitor { init(onChange: @escaping () -> Void) {}; func start() {} }
final class PowerMonitor { init(onWake: @escaping () -> Void) {}; func start() {} }
enum Verifier {
    static func probeGatewayHealth(ip: String, port: Int) async -> Bool { true }
    static func probeGatewayUntil(ip: String, port: Int, deadline: Date) async -> Bool { true }
}
enum UapiClient { static func latestHandshakeEpoch(iface: String) -> Int? { Int(Date().timeIntervalSince1970) } }
struct WireGuardConfig {
    let searchDomains: [String] = []
    static func parse(_ content: String) throws -> WireGuardConfig { .init() }
}
enum Diagnostics { static func collectBundle(apiKey: String) -> Data { Data() } }

actor BackendClient {
    var baseUrl = "https://example.invalid"
    var isConfigured = true
    var response: [String: Any]?
    var onWan: (() async -> Void)?
    private var standalone = false
    func setResponse(_ value: [String: Any]?) { response = value }
    func configure(_ value: Bool) { isConfigured = value }
    func setWanHook(_ hook: @escaping () async -> Void) { onWan = hook }
    func setBaseUrl(_ dns: String?, privileged: Bool = false) -> String? {
        guard privileged, !isConfigured, let dns, !dns.isEmpty else { return nil }
        baseUrl = "https://" + dns; isConfigured = true; return baseUrl
    }
    func setStandaloneModeChosen(_ value: Bool) { standalone = value }
    func getStandaloneModeChosen() -> Bool { standalone }
    func setChannel(_ value: String?) {}
    func setApiKey(_ value: String?) {}
    func currentApiKey() -> String { "synthetic" }
    func register(clientId: UUID, hostname: String, username: String, profiles: [String], trayRunning: Bool) -> [String: Any]? { response }
    func poll(clientId: UUID, trayRunning: Bool) -> [String: Any]? { response }
    func getWanIp() async -> String? { await onWan?(); return nil }
    func logEvent(clientId: UUID, eventType: String, username: String, tunnelName: String, lanIp: String? = nil, wanIp: String? = nil) {}
    func confirmMfaEnrollment(clientId: UUID, code: String) -> Bool { false }
    func notifyOffline(clientId: UUID) {}
    func getProfiles(clientId: UUID) -> [(fileName: String, content: String)]? { [] }
    func getPendingConfig(clientId: UUID) -> [String: Any]? { nil }
    func uploadLogs(clientId: UUID, gzData: Data, trigger: String) -> Bool { true }
}
actor ConfigManager {
    var names = ["native", "foreign"]
    var staged: [String] = []
    private var userProfiles: [String: [String]] = [:]
    private(set) var deletedUsers: [String] = []
    func setProfiles(username: String, names: [String]) { userProfiles[username] = names }
    func profiles(for username: String) -> [String] { userProfiles[username] ?? names }
    func allProfiles() -> [String] { names }
    func deletableProfiles(for username: String) -> [String] { names }
    func orderProfiles(_ profiles: [String], serverProfileName: String?) -> [String] { profiles }
    func hasStagedConfig() -> Bool { !staged.isEmpty }
    func stagedProfileNames() -> [String] { staged }
    func setStaged(_ value: [String]) { staged = value }
    func claimStagedConfig(username: String) {}
    func isManaged(_ username: String, _ profile: String) -> Bool { false }
    func deleteUserProfile(username: String, profile: String) -> Bool { deletedUsers.append(username); return true }
    func deleteProfile(_ profile: String) {}
    func saveConfig(username: String, profile: String, content: String) throws {}
    func saveStagedConfig(filename: String, content: String) {}
    func saveDeviceTunnelConfig(profile: String, content: String) throws {}
    func decryptedContent(username: String, profile: String) throws -> String { "synthetic-config-\(username)" }
    func decryptedContentByTunnel(_ profile: String) throws -> String { "synthetic-config" }
    func decryptedDeviceTunnelContent(profile: String) throws -> String { "synthetic-config" }
    func findAllowedIpsConflict(newProfileUser: String, newProfile: String, activeTunnels: [String]) -> String? { nil }
    func findLocalLanConflict(newProfileUser: String, newProfile: String, remoteLanCidrs: [String]) -> String? { nil }
}
actor AutoConnectConfig {
    private var enabled = false
    private var name: String?
    private var networks: [TrustedNetwork] = []
    func isEnabled() -> Bool { enabled }
    func getProfileName() -> String? { name }
    func getTrustedNetworks() -> [TrustedNetwork] { networks }
    func setUserEnabled(_ value: Bool) { enabled = value }
    func persist() {}
    func updateFromBackend(adminEnabled: Bool, enabled: Bool, profileName: String?, trustedNetworks: [TrustedNetwork]) {
        self.enabled = enabled; name = profileName; networks = trustedNetworks
    }
}
final class TunnelEngine {
    var starts = 0
    var configs: [String] = []
    var stops = 0
    var startFailures = 0
    var beforeStart: (() -> Void)?
    func up(profile: String, config: String) throws -> Int {
        starts += 1; configs.append(config); beforeStart?()
        if startFailures > 0 { startFailures -= 1; throw CocoaError(.fileReadUnknown) }
        return starts
    }
    func down(profile: String, iface: String?, searchDomains: [String]) { stops += 1 }
    func iface(for profile: String) -> String? { "fake" }
    func reconcile() -> [(profile: String, iface: String)] { [] }
}
struct UpdateResult { let available = false; let latest: String? = nil }
actor Updater { func check() -> UpdateResult { .init() }; func checkAndApply() {} }
