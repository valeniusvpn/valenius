/// Case-insensitive view over a decoded JSON map — the Dart analogue of
/// `ServiceJsonContext`'s `PropertyNameCaseInsensitive`. The backend serializes
/// camelCase; reading case-insensitively keeps us robust to casing drift.
class CiMap {
  CiMap(Map<String, dynamic> source)
      : _map = {
          for (final e in source.entries) e.key.toLowerCase(): e.value,
        };

  final Map<String, dynamic> _map;

  T? get<T>(String key) => _map[key.toLowerCase()] as T?;
  bool boolean(String key) => get<bool>(key) ?? false;
  String? string(String key) => get<String>(key);
  int? integer(String key) => get<num>(key)?.toInt();
  List<dynamic> list(String key) => get<List<dynamic>>(key) ?? const [];
}

/// Mirrors the backend `StatusResponse` (`ClientsController.cs`). NOTE: the
/// heartbeat does **not** carry a profile list — the available profiles are
/// derived client-side from the configs this device has received and stored
/// (see the config store). The heartbeat only advertises *pending* configs to
/// fetch/install, plus activation/MFA/connect state. Unknown/missing fields
/// degrade to false/null so older backends stay compatible.
class StatusResponse {
  StatusResponse({
    required this.isActive,
    required this.hasPendingConfig,
    required this.autoConnectEnabled,
    required this.autoConnectProfileName,
    required this.serverProfileName,
    required this.serverVpnIp,
    required this.pendingConnect,
    required this.pendingDisconnect,
    required this.duplicatePending,
    required this.mfaRequired,
    required this.mfaAuthorizeUrl,
    required this.mfaEnrollmentUri,
    required this.mfaEnrollmentOpen,
    required this.mfaSessionExpiresAt,
    required this.gatewayProfiles,
    required this.pendingForeignConfigs,
    required this.mfaApproveNumber,
    required this.pendingApprovals,
    required this.logUploadRequested,
    required this.remoteLanCidrs,
    this.clientApiKey,
  });

  factory StatusResponse.fromJson(Map<String, dynamic> json) {
    final m = CiMap(json);
    return StatusResponse(
      isActive: m.boolean('isActive'),
      hasPendingConfig: m.boolean('hasPendingConfig'),
      autoConnectEnabled: m.boolean('autoConnectEnabled'),
      autoConnectProfileName: m.string('autoConnectProfileName'),
      serverProfileName: m.string('serverProfileName'),
      serverVpnIp: m.string('serverVpnIp'),
      pendingConnect: m.boolean('pendingConnect'),
      pendingDisconnect: m.boolean('pendingDisconnect'),
      duplicatePending: m.boolean('duplicatePending'),
      mfaRequired: m.boolean('mfaRequired'),
      mfaAuthorizeUrl: m.string('mfaAuthorizeUrl'),
      mfaEnrollmentUri: m.string('mfaEnrollmentUri'),
      mfaEnrollmentOpen: m.boolean('mfaEnrollmentOpen'),
      mfaSessionExpiresAt: DateTime.tryParse(m.string('mfaSessionExpiresAt') ?? ''),
      gatewayProfiles: m
          .list('gatewayProfiles')
          .map((e) => GatewayProfile.fromJson(e as Map<String, dynamic>))
          .toList(),
      pendingForeignConfigs: m
          .list('pendingForeignConfigs')
          .map((e) => PendingConfig.fromJson(e as Map<String, dynamic>))
          .toList(),
      mfaApproveNumber: m.integer('mfaApproveNumber'),
      pendingApprovals: m
          .list('pendingApprovals')
          .map((e) => PendingApproval.fromJson(e as Map<String, dynamic>))
          .toList(),
      logUploadRequested: m.boolean('logUploadRequested'),
      remoteLanCidrs: m.list('remoteLanCidrs').map((e) => e as String).toList(),
      clientApiKey: m.string('clientApiKey'),
    );
  }

  final bool isActive;
  final bool hasPendingConfig;
  final bool autoConnectEnabled;
  final String? autoConnectProfileName;
  final String? serverProfileName;
  final String? serverVpnIp;
  final bool pendingConnect;
  final bool pendingDisconnect;
  final bool duplicatePending;
  final bool mfaRequired;
  final String? mfaAuthorizeUrl;
  final String? mfaEnrollmentUri;
  final bool mfaEnrollmentOpen;
  final DateTime? mfaSessionExpiresAt;
  final List<GatewayProfile> gatewayProfiles;
  final List<PendingConfig> pendingForeignConfigs;

  /// For a gated requester with a push-approver: the number to display.
  final int? mfaApproveNumber;

  /// For an approver phone: approvals it must act on.
  final List<PendingApproval> pendingApprovals;

  /// When true the client uploads its (app-only, redacted) logs to the backend.
  final bool logUploadRequested;

  /// Physical LAN CIDR(s) behind a Valenius-managed sidecar, self-reported by the sidecar.
  /// Used by the pre-connect LAN-conflict check to detect the remote network even for
  /// full-tunnel (0.0.0.0/0) profiles. Empty when unknown (External customers, or a sidecar
  /// not yet upgraded).
  final List<String> remoteLanCidrs;

  /// New client API key sent during a backend key rotation (only while this device is
  /// still on the previous key). The app persists it to secure storage and uses it for
  /// subsequent requests. Null in the normal case.
  final String? clientApiKey;
}

/// A cross-device approval this phone must act on (it's the approver).
class PendingApproval {
  PendingApproval({
    required this.challengeId,
    required this.requesterName,
    required this.requesterIp,
    required this.choices,
    required this.expiresAt,
  });

  factory PendingApproval.fromJson(Map<String, dynamic> json) {
    final m = CiMap(json);
    return PendingApproval(
      challengeId: m.integer('challengeId') ?? 0,
      requesterName: m.string('requesterName') ?? 'Unknown device',
      requesterIp: m.string('requesterIp'),
      choices: m.list('choices').map((e) => (e as num).toInt()).toList(),
      expiresAt: DateTime.tryParse(m.string('expiresAt') ?? '') ?? DateTime.now(),
    );
  }

  final int challengeId;
  final String requesterName;
  final String? requesterIp;
  final List<int> choices;
  final DateTime expiresAt;
}

/// Per-profile gateway probe target (`GatewayProfileEntry`; Pro, transit-unique
/// sidecars only).
class GatewayProfile {
  GatewayProfile({
    required this.profileName,
    required this.gatewayIp,
    required this.healthPort,
  });

  factory GatewayProfile.fromJson(Map<String, dynamic> json) {
    final m = CiMap(json);
    return GatewayProfile(
      profileName: m.string('profileName') ?? '',
      gatewayIp: m.string('gatewayIp') ?? '',
      healthPort: m.integer('healthPort') ?? 0,
    );
  }

  final String profileName;
  final String gatewayIp;
  final int healthPort;
}

/// A staged config delivered inline (`PendingConfigResponse` /
/// `PendingForeignConfigEntry` — both are `{ FileName, Content }`).
class PendingConfig {
  PendingConfig({required this.fileName, required this.content});

  factory PendingConfig.fromJson(Map<String, dynamic> json) {
    final m = CiMap(json);
    return PendingConfig(
      fileName: m.string('fileName') ?? '',
      content: m.string('content') ?? '',
    );
  }

  final String fileName;
  final String content;
}
