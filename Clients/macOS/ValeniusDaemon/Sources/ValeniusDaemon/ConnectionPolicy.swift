import Foundation

enum AuthorizationDate {
    static func parse(_ raw: String?) -> Date? {
        guard var raw, !raw.isEmpty else { return nil }
        // Older clients used naive UTC. Explicit offsets must remain meaningful.
        if !raw.hasSuffix("Z") && raw.range(of: "[+-][0-9]{2}:[0-9]{2}$", options: .regularExpression) == nil {
            raw += "Z"
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: raw) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: raw)
    }
    static func string(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}

struct ConnectionPolicy: Codable {
    struct RemoteLan: Codable {
        let profileName: String
        let cidrs: [String]
        enum CodingKeys: String, CodingKey { case profileName = "ProfileName", cidrs = "Cidrs" }
    }
    struct Fallback: Codable {
        let profileName: String
        let port: Int
        let triggerSeconds: Int
        enum CodingKeys: String, CodingKey {
            case profileName = "ProfileName", port = "Port", triggerSeconds = "TriggerSeconds"
        }
    }
    let version: Int
    let serverProfileName: String?
    let serverVpnIp: String?
    let serverHealthUrl: String?
    let deviceTunnelEnabled: Bool
    let skipConnectivityCheck: Bool
    let remoteLanCidrsByProfile: [RemoteLan]
    let fallbackEndpoints: [Fallback]
    let mfaRequired: Bool
    let mfaAuthorizeUrl: String?
    let mfaSessionExpiresAt: String?
    let mfaEnrollmentOpen: Bool

    enum CodingKeys: String, CodingKey, CaseIterable {
        case version = "Version", serverProfileName = "ServerProfileName", serverVpnIp = "ServerVpnIp"
        case serverHealthUrl = "ServerHealthUrl", deviceTunnelEnabled = "DeviceTunnelEnabled"
        case skipConnectivityCheck = "SkipConnectivityCheck", remoteLanCidrsByProfile = "RemoteLanCidrsByProfile"
        case fallbackEndpoints = "FallbackEndpoints", mfaRequired = "MfaRequired", mfaAuthorizeUrl = "MfaAuthorizeUrl"
        case mfaSessionExpiresAt = "MfaSessionExpiresAt", mfaEnrollmentOpen = "MfaEnrollmentOpen"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        // Missing nullable fields must not turn an incomplete cache into non-MFA access.
        for key in CodingKeys.allCases where !c.contains(key) {
            throw DecodingError.keyNotFound(key, .init(codingPath: decoder.codingPath, debugDescription: "Incomplete connection policy"))
        }
        version = try c.decode(Int.self, forKey: .version)
        guard version == 1 else { throw CocoaError(.fileReadCorruptFile) }
        serverProfileName = try c.decodeIfPresent(String.self, forKey: .serverProfileName)
        serverVpnIp = try c.decodeIfPresent(String.self, forKey: .serverVpnIp)
        serverHealthUrl = try c.decodeIfPresent(String.self, forKey: .serverHealthUrl)
        deviceTunnelEnabled = try c.decode(Bool.self, forKey: .deviceTunnelEnabled)
        skipConnectivityCheck = try c.decode(Bool.self, forKey: .skipConnectivityCheck)
        remoteLanCidrsByProfile = try c.decode([RemoteLan].self, forKey: .remoteLanCidrsByProfile)
        fallbackEndpoints = try c.decode([Fallback].self, forKey: .fallbackEndpoints)
        mfaRequired = try c.decode(Bool.self, forKey: .mfaRequired)
        mfaAuthorizeUrl = try c.decodeIfPresent(String.self, forKey: .mfaAuthorizeUrl)
        mfaSessionExpiresAt = try c.decodeIfPresent(String.self, forKey: .mfaSessionExpiresAt)
        mfaEnrollmentOpen = try c.decode(Bool.self, forKey: .mfaEnrollmentOpen)
        if let raw = mfaSessionExpiresAt, AuthorizationDate.parse(raw) == nil { throw CocoaError(.fileReadCorruptFile) }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(version, forKey: .version)
        try c.encode(serverProfileName, forKey: .serverProfileName)
        try c.encode(serverVpnIp, forKey: .serverVpnIp)
        try c.encode(serverHealthUrl, forKey: .serverHealthUrl)
        try c.encode(deviceTunnelEnabled, forKey: .deviceTunnelEnabled)
        try c.encode(skipConnectivityCheck, forKey: .skipConnectivityCheck)
        try c.encode(remoteLanCidrsByProfile, forKey: .remoteLanCidrsByProfile)
        try c.encode(fallbackEndpoints, forKey: .fallbackEndpoints)
        try c.encode(mfaRequired, forKey: .mfaRequired)
        try c.encode(mfaAuthorizeUrl, forKey: .mfaAuthorizeUrl)
        try c.encode(mfaSessionExpiresAt, forKey: .mfaSessionExpiresAt)
        try c.encode(mfaEnrollmentOpen, forKey: .mfaEnrollmentOpen)
    }

    static func fromBackend(_ response: [String: Any]) throws -> ConnectionPolicy {
        let j = JsonObject(response)
        var normalized: [String: Any] = ["Version": 1]
        for key in ["ServerProfileName", "ServerVpnIp", "ServerHealthUrl", "MfaAuthorizeUrl", "MfaSessionExpiresAt"] {
            normalized[key] = j.value(key) ?? NSNull()
        }
        for key in ["DeviceTunnelEnabled", "SkipConnectivityCheck", "MfaRequired", "MfaEnrollmentOpen"] {
            normalized[key] = j.value(key) ?? false
        }
        for (key, fields) in [("RemoteLanCidrsByProfile", ["ProfileName", "Cidrs"]),
                              ("FallbackEndpoints", ["ProfileName", "Port", "TriggerSeconds"])] {
            let raw = j.value(key)
            if raw != nil && !(raw is NSNull) && j.array(key) == nil { throw CocoaError(.fileReadCorruptFile) }
            let rows: [Any] = j.array(key) ?? []
            let entries: [[String: Any]] = try rows.map { (item: Any) -> [String: Any] in
                guard let item = item as? [String: Any] else { throw CocoaError(.fileReadCorruptFile) }
                let row = JsonObject(item)
                var output: [String: Any] = [:]
                for field in fields {
                    output[field] = row.value(field) ?? (field == "TriggerSeconds" ? 20 : NSNull())
                }
                return output
            }
            normalized[key] = entries
        }
        return try JSONDecoder().decode(ConnectionPolicy.self, from: JSONSerialization.data(withJSONObject: normalized))
    }

    func requiresMfa(at now: Date) -> Bool {
        mfaRequired || (AuthorizationDate.parse(mfaSessionExpiresAt).map { now >= $0 } ?? false)
    }
}

struct CachedRegistration: Codable {
    var clientId: UUID
    var isActive: Bool = false
    var lastCheckedUtc: String?
    var standaloneModeChosen: Bool = false
    var connectionPolicy: ConnectionPolicy?

    enum CodingKeys: String, CodingKey {
        case clientId = "ClientId", isActive = "IsActive", lastCheckedUtc = "LastCheckedUtc"
        case standaloneModeChosen = "StandaloneModeChosen", connectionPolicy = "ConnectionPolicy"
    }
    init(clientId: UUID = UUID()) { self.clientId = clientId }
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        clientId = try c.decode(UUID.self, forKey: .clientId)
        isActive = try c.decodeIfPresent(Bool.self, forKey: .isActive) ?? false
        lastCheckedUtc = try? c.decodeIfPresent(String.self, forKey: .lastCheckedUtc)
        standaloneModeChosen = try c.decodeIfPresent(Bool.self, forKey: .standaloneModeChosen) ?? false
        // Bad authorization keeps a valid identity, requiring a live refresh.
        connectionPolicy = try? c.decodeIfPresent(ConnectionPolicy.self, forKey: .connectionPolicy)
    }
    func activationAllowed(at now: Date, days: Int) -> Bool {
        guard isActive, connectionPolicy != nil, let checked = AuthorizationDate.parse(lastCheckedUtc) else { return false }
        return checked <= now.addingTimeInterval(300) && now.timeIntervalSince(checked) < Double(days) * 86400
    }
    func profileAllowed(_ profile: String, at now: Date, days: Int) -> Bool {
        guard activationAllowed(at: now, days: days), let policy = connectionPolicy else { return false }
        return !policy.requiresMfa(at: now) || (policy.serverProfileName?.isEmpty == false && policy.serverProfileName != profile)
    }
    func expiresAt(days: Int) -> Date? {
        guard isActive, connectionPolicy != nil, let checked = AuthorizationDate.parse(lastCheckedUtc) else { return nil }
        return checked.addingTimeInterval(Double(days) * 86400)
    }
}
