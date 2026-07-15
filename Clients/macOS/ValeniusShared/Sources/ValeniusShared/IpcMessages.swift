// Mirrors Valenius.Shared.IpcMessages (Shared/Valenius.Shared/IpcMessages.cs) and its
// Python port (Clients/Linux/shared/messages.py). No code generation across the repos —
// keep these three in lockstep by hand. JSON field names are PascalCase to match .NET's
// default serialization; CommandType serializes as its underlying Int, not a string.

import Foundation

public enum CommandType: Int, Codable, Sendable {
    case connect = 0
    case disconnect = 1
    case status = 2
    case uploadConfig = 3
    case getConfigInfo = 4
    case checkUpdate = 5
    case register = 6
    case syncStatus = 7
    case setAutoConnect = 8
    case deleteProfile = 9
    /// Sent by the app just before it exits so the daemon can notify the backend.
    case notifyOffline = 10
    /// Sent by the app to confirm MFA enrollment with a TOTP code (carried in `PipeCommand.mfaCode`).
    case mfaEnrollConfirm = 11
    /// Sent by the app's "Send logs" action — the daemon collects its (Valenius-only,
    /// redacted) diagnostic logs and uploads them to the backend.
    case sendLogs = 12
}

public struct PipeCommand: Codable, Sendable {
    public var command: CommandType
    public var configContent: String?
    public var profileName: String?
    public var autoConnectEnabled: Bool?
    /// TOTP code for `CommandType.mfaEnrollConfirm`.
    public var mfaCode: String?

    public init(
        command: CommandType,
        configContent: String? = nil,
        profileName: String? = nil,
        autoConnectEnabled: Bool? = nil,
        mfaCode: String? = nil
    ) {
        self.command = command
        self.configContent = configContent
        self.profileName = profileName
        self.autoConnectEnabled = autoConnectEnabled
        self.mfaCode = mfaCode
    }

    enum CodingKeys: String, CodingKey {
        case command = "Command"
        case configContent = "ConfigContent"
        case profileName = "ProfileName"
        case autoConnectEnabled = "AutoConnectEnabled"
        case mfaCode = "MfaCode"
    }
}

public struct PipeResponse: Codable, Sendable {
    public var success: Bool
    public var error: String?
    public var dataJson: String?

    public init(success: Bool, error: String? = nil, dataJson: String? = nil) {
        self.success = success
        self.error = error
        self.dataJson = dataJson
    }

    enum CodingKeys: String, CodingKey {
        case success = "Success"
        case error = "Error"
        case dataJson = "DataJson"
    }

    public static func ok(dataJson: String? = nil) -> PipeResponse {
        PipeResponse(success: true, dataJson: dataJson)
    }

    public static func ok<T: Encodable>(_ value: T) -> PipeResponse {
        let json = (try? String(data: JSONEncoder().encode(value), encoding: .utf8)) ?? nil
        return PipeResponse(success: true, dataJson: json)
    }

    public static func fail(_ message: String) -> PipeResponse {
        PipeResponse(success: false, error: message)
    }
}

/// One connected WireGuard tunnel. Multiple may be active simultaneously (the
/// primary/server VPN plus any number of foreign-customer profiles).
public struct ConnectedTunnelInfo: Codable, Sendable {
    public var name: String
    public var isVerified: Bool
    public var connectedUser: String?
    /// ISO-8601 string (matches .NET DateTime / Python isoformat on the wire).
    public var connectedSince: String?

    public init(name: String, isVerified: Bool = false, connectedUser: String? = nil, connectedSince: String? = nil) {
        self.name = name
        self.isVerified = isVerified
        self.connectedUser = connectedUser
        self.connectedSince = connectedSince
    }

    enum CodingKeys: String, CodingKey {
        case name = "Name"
        case isVerified = "IsVerified"
        case connectedUser = "ConnectedUser"
        case connectedSince = "ConnectedSince"
    }
}

public struct TunnelStatus: Codable, Sendable {
    public var isConnected: Bool = false
    /// True once the background probe confirmed end-to-end traffic through the tunnel.
    /// The UI replaces the green dot with a green checkmark when this is true.
    public var isVerified: Bool = false
    public var connectedUser: String?
    public var tunnelName: String?
    public var connectedSince: String?

    /// All currently-connected tunnels. The source of truth for per-profile row
    /// indicators in the menu bar app. The single-tunnel fields above mirror the
    /// primary/server profile entry here (or the first entry) for backward compat.
    public var connectedTunnels: [ConnectedTunnelInfo] = []
    public var registrationIsActive: Bool?
    public var hasStagedConfig: Bool = false
    public var updateAvailable: Bool = false
    public var availableProfiles: [String] = []
    public var deletableProfiles: [String] = []
    public var autoConnectEnabled: Bool = false

    // MARK: MFA session gating (Pro)

    /// True when this peer is MFA-gated and has no active session — the app prompts for re-auth.
    public var mfaRequired: Bool = false
    /// Deep link the app opens in the browser to authorize.
    public var mfaAuthorizeUrl: String?
    /// Absolute expiry of the active MFA session, for the countdown. Nil when none.
    public var mfaSessionExpiresAt: String?
    /// True when the TOTP enrollment window is open — the app shows the QR.
    public var mfaEnrollmentOpen: Bool = false
    /// otpauth:// URI for the enrollment QR.
    public var mfaEnrollmentUri: String?
    /// For a gated requester with a push-approver device: the number to display
    /// ("approve N on your phone"). Nil when there's no active push-approve challenge.
    public var mfaApproveNumber: Int?

    /// Daemon-derived (the app can't read the root-owned appsettings.json).
    public var backendUrl: String?
    public var backendReachable: Bool?

    public init() {}

    enum CodingKeys: String, CodingKey {
        case isConnected = "IsConnected"
        case isVerified = "IsVerified"
        case connectedUser = "ConnectedUser"
        case tunnelName = "TunnelName"
        case connectedSince = "ConnectedSince"
        case connectedTunnels = "ConnectedTunnels"
        case registrationIsActive = "RegistrationIsActive"
        case hasStagedConfig = "HasStagedConfig"
        case updateAvailable = "UpdateAvailable"
        case availableProfiles = "AvailableProfiles"
        case deletableProfiles = "DeletableProfiles"
        case autoConnectEnabled = "AutoConnectEnabled"
        case mfaRequired = "MfaRequired"
        case mfaAuthorizeUrl = "MfaAuthorizeUrl"
        case mfaSessionExpiresAt = "MfaSessionExpiresAt"
        case mfaEnrollmentOpen = "MfaEnrollmentOpen"
        case mfaEnrollmentUri = "MfaEnrollmentUri"
        case mfaApproveNumber = "MfaApproveNumber"
        case backendUrl = "BackendUrl"
        case backendReachable = "BackendReachable"
    }
}

public struct RegistrationResult: Codable, Sendable {
    public var isActive: Bool
    public var message: String?

    public init(isActive: Bool, message: String? = nil) {
        self.isActive = isActive
        self.message = message
    }

    enum CodingKeys: String, CodingKey {
        case isActive = "IsActive"
        case message = "Message"
    }
}

public struct ConfigInfo: Codable, Sendable {
    public var hasConfig: Bool = false
    public var tunnelName: String?
    public var fileName: String?
    public var profiles: [String] = []

    public init(hasConfig: Bool = false, tunnelName: String? = nil, fileName: String? = nil, profiles: [String] = []) {
        self.hasConfig = hasConfig
        self.tunnelName = tunnelName
        self.fileName = fileName
        self.profiles = profiles
    }

    enum CodingKeys: String, CodingKey {
        case hasConfig = "HasConfig"
        case tunnelName = "TunnelName"
        case fileName = "FileName"
        case profiles = "Profiles"
    }
}

public struct VersionCheckResult: Codable, Sendable {
    public var updateAvailable: Bool = false
    public var currentVersion: String?
    public var latestVersion: String?
    public var releaseNotes: String?

    public init(updateAvailable: Bool = false, currentVersion: String? = nil, latestVersion: String? = nil, releaseNotes: String? = nil) {
        self.updateAvailable = updateAvailable
        self.currentVersion = currentVersion
        self.latestVersion = latestVersion
        self.releaseNotes = releaseNotes
    }

    enum CodingKeys: String, CodingKey {
        case updateAvailable = "UpdateAvailable"
        case currentVersion = "CurrentVersion"
        case latestVersion = "LatestVersion"
        case releaseNotes = "ReleaseNotes"
    }
}

public enum ProfileNameHelper {
    public static func isValid(_ name: String?) -> Bool {
        guard let name, !name.isEmpty, name.count <= 50 else { return false }
        return name.allSatisfy { $0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" }
    }

    public static func sanitize(_ filename: String) -> String {
        let stem = (filename as NSString).deletingPathExtension
        let safe = String(stem.map { $0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" ? $0 : "_" })
        return String(safe.prefix(50))
    }
}
