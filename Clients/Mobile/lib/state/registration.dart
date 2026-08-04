import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/backend_client.dart';
import '../api/identity.dart';
import '../api/models.dart';
import '../config/config_store.dart';
import '../config/profile_name.dart';
import '../config/wg_normalize.dart' show FallbackTarget;
import '../diagnostics/diagnostics.dart';
import '../platform/device_info.dart';
import '../platform/health_probe.dart';
import '../platform/key_store.dart';
import '../platform/push.dart';
import 'app_state.dart';

final keyStoreProvider = Provider<KeyStore>((_) => SecureKeyStore());

/// Secure-storage key under which the installation's client API key is persisted. The app ships with
/// no key; it's obtained at onboarding (QR pairing returns it, or it's delivered on admin activation)
/// and read on launch by [RegistrationController.start]. Shared with the first-run setup flow.
const kClientApiKeyStorageKey = 'valenius.clientApiKey';

/// Coerce a user-entered DNS / scanned URL to the canonical https form the client assumes:
/// strip any scheme, drop path/query and surrounding whitespace or slashes, then prepend
/// https:// . Returns '' when no host remains. Mirrors the desktop BackendUrlProvider.Normalize.
String normalizeBackendUrl(String raw) {
  var s = raw.trim();
  if (s.isEmpty) return '';
  final idx = s.indexOf('://');
  if (idx >= 0) s = s.substring(idx + 3);
  s = s.replaceFirst(RegExp(r'^/+'), '');
  final cut = s.indexOf(RegExp(r'[/?#]'));
  if (cut >= 0) s = s.substring(0, cut);
  s = s.trim().replaceFirst(RegExp(r'\.+$'), '');
  return s.isEmpty ? '' : 'https://$s';
}

/// Backend URL state: null [url] means unconfigured (fresh install — the app has no backend
/// baked in). [loaded] is false until the persisted value has been read from secure storage,
/// so the root gate can show a splash instead of flashing the setup screen on every launch.
class BackendUrlState {
  const BackendUrlState({this.url, this.loaded = false});

  final String? url;
  final bool loaded;

  bool get configured => url != null && url!.isNotEmpty;
}

/// Owns the backend URL. The app is published with **no** backend URL; the user provides it at
/// first run (scan QR / type it / triple-tap easter egg — see setup_screen.dart). Persisted to
/// secure storage so it survives restarts; a set value drives [backendBaseUrlProvider], which the
/// registration controller watches, so configuring the URL brings the whole app online.
class BackendUrlController extends StateNotifier<BackendUrlState> {
  BackendUrlController(this._keyStore) : super(const BackendUrlState()) {
    _load();
  }

  final KeyStore _keyStore;
  static const _storageKey = 'valenius.backendUrl';

  Future<void> _load() async {
    final stored = await _keyStore.read(_storageKey);
    state = BackendUrlState(
      url: (stored != null && stored.isNotEmpty) ? stored : null,
      loaded: true,
    );
  }

  /// Set from a user-entered DNS or a scanned URL. Normalizes to https://host, persists, switches.
  /// Returns the normalized URL, or null if the input has no usable host.
  Future<String?> set(String raw) async {
    final normalized = normalizeBackendUrl(raw);
    if (normalized.isEmpty) return null;
    await _keyStore.write(_storageKey, normalized);
    state = BackendUrlState(url: normalized, loaded: true);
    return normalized;
  }
}

final backendUrlControllerProvider =
    StateNotifierProvider<BackendUrlController, BackendUrlState>(
  (ref) => BackendUrlController(ref.watch(keyStoreProvider)),
);

/// Backend base URL consumed by the registration controller / backend client. Empty string when
/// unconfigured (the root gate shows the setup screen instead of mounting the home screen then).
final backendBaseUrlProvider =
    Provider<String>((ref) => ref.watch(backendUrlControllerProvider).url ?? '');

final deviceInfoProvider =
    Provider<DeviceInfoSource>((_) => PlatformDeviceInfo());

final pushTokensProvider = Provider<PushTokens>((_) => FcmPushTokens());

final configStoreProvider =
    Provider<ConfigStore>((ref) => SecureConfigStore(ref.watch(keyStoreProvider)));

/// Heartbeat interval. Null disables polling (used by tests so no timer leaks).
final registrationPollIntervalProvider =
    Provider<Duration?>((_) => const Duration(seconds: 60));

/// Builds a [BackendApi] for a (baseUrl, clientKey). Overridden in tests.
final backendFactoryProvider =
    Provider<BackendApi Function(String baseUrl, String clientKey)>(
  (_) => (url, key) => BackendClient(baseUrl: url, clientKey: key),
);

/// Per-profile gateway probe targets from the latest heartbeat
/// (`StatusResponse.GatewayProfiles[]`). Empty when none are advertised, in
/// which case verification falls back to the handshake check.
final gatewayTargetsProvider =
    StateProvider<Map<String, GatewayTarget>>((_) => const {});

/// Per-profile UDP fallback port + trigger window from the latest heartbeat
/// (`StatusResponse.fallbackEndpoints`). Empty when none are advertised (no customer
/// opt-in, or opted-in but not yet confirmed active by the sidecar).
final fallbackTargetsProvider =
    StateProvider<Map<String, FallbackTarget>>((_) => const {});

/// Per-profile physical LAN CIDR(s) behind that profile's own sidecar, from the latest
/// heartbeat (`StatusResponse.RemoteLanCidrsByProfile[]`). Used by the pre-connect
/// LAN-conflict check to detect the remote network for full-tunnel profiles. Keyed by
/// profile name — the native server profile and each foreign profile each report their
/// OWN source customer's LAN, so never apply one profile's entry to another's connect
/// (a real bug found in testing: a native sidecar's conflicting LAN wrongly blocked an
/// unrelated foreign profile). Empty/missing entry when unknown for that profile.
final remoteLanCidrsProvider =
    StateProvider<Map<String, List<String>>>((_) => const {});

/// TOTP MFA state from the latest heartbeat. `required` means the peer is gated
/// and needs authorization before it will connect.
class MfaState {
  const MfaState({
    this.required = false,
    this.authorizeUrl,
    this.enrollmentOpen = false,
    this.enrollmentUri,
    this.sessionExpiresAt,
  });

  final bool required;
  final String? authorizeUrl;
  final bool enrollmentOpen;
  final String? enrollmentUri;
  final DateTime? sessionExpiresAt;
}

final mfaStateProvider = StateProvider<MfaState>((_) => const MfaState());

/// Cross-device approvals this phone (as approver) must act on, from the heartbeat.
final pendingApprovalsProvider =
    StateProvider<List<PendingApproval>>((_) => const []);

/// What happened to a profile as a result of the latest heartbeat ingest —
/// consumed by the UI to show a one-shot notification (see home_screen.dart).
enum ProfileChangeKind { added, updated, deleted }

class ProfileChangeEvent {
  ProfileChangeEvent(this.name, this.kind) : at = DateTime.now();

  final String name;
  final ProfileChangeKind kind;

  /// Distinguishes two otherwise-identical events (same name+kind on separate
  /// heartbeats) so `ref.listen` fires again instead of treating a repeat as
  /// "no change" — see [ProfilesController] for the equivalent list refresh.
  final DateTime at;
}

/// Most recent profile add/update/delete detected by the heartbeat ingest, or
/// null before the first one. Overwritten (not queued) — a UI that's been
/// backgrounded through several changes only sees the latest.
final profileChangeEventProvider =
    StateProvider<ProfileChangeEvent?>((_) => null);

/// The profile list shown in the UI — derived from the configs this device has
/// received and stored (the heartbeat carries no profile list).
final profilesControllerProvider =
    StateNotifierProvider<ProfilesController, List<ProfileMeta>>((ref) {
  final controller = ProfilesController(ref.watch(configStoreProvider));
  controller.load();
  return controller;
});

final registrationControllerProvider =
    StateNotifierProvider<RegistrationController, RegistrationState>((ref) {
  final controller = RegistrationController(
    baseUrl: ref.watch(backendBaseUrlProvider),
    keyStore: ref.watch(keyStoreProvider),
    deviceInfo: ref.watch(deviceInfoProvider),
    pushTokens: ref.watch(pushTokensProvider),
    configStore: ref.watch(configStoreProvider),
    backendFactory: ref.watch(backendFactoryProvider),
    pollInterval: ref.watch(registrationPollIntervalProvider),
    onConfigsChanged: () =>
        ref.read(profilesControllerProvider.notifier).load(),
    onGatewayTargets: (targets) =>
        ref.read(gatewayTargetsProvider.notifier).state = targets,
    onFallbackTargets: (targets) =>
        ref.read(fallbackTargetsProvider.notifier).state = targets,
    onRemoteLanCidrs: (cidrs) =>
        ref.read(remoteLanCidrsProvider.notifier).state = cidrs,
    onMfaState: (mfa) => ref.read(mfaStateProvider.notifier).state = mfa,
    onMfaGate: (gatedProfile) =>
        ref.read(connectionControllerProvider.notifier).onMfaGate(gatedProfile),
    onProfileDeleted: (name) =>
        ref.read(connectionControllerProvider.notifier).onProfileDeleted(name),
    onProfileChange: (event) =>
        ref.read(profileChangeEventProvider.notifier).state = event,
    onPendingApprovals: (list) =>
        ref.read(pendingApprovalsProvider.notifier).state = list,
    onAutoConnect: (enabled, profileName, configText) =>
        ref.read(connectionControllerProvider.notifier).applyOnDemand(
              enabled: enabled,
              profileName: profileName,
              configText: configText,
            ),
  );
  controller.start();
  return controller;
});

enum RegStatus { loading, awaitingActivation, active, duplicate, error }

class ProfileMeta {
  const ProfileMeta({
    required this.name,
    this.deletable = false,
    this.mfaGated = false,
  });

  final String name;
  final bool deletable;
  final bool mfaGated;
}

class ProfilesController extends StateNotifier<List<ProfileMeta>> {
  ProfilesController(this._store) : super(const []);

  final ConfigStore _store;

  Future<void> load() async {
    final stored = await _store.list();
    state = [
      for (final p in stored)
        // TODO(milestone 7): per-profile MFA gating.
        ProfileMeta(name: p.name, deletable: p.deletable),
    ];
  }
}

class RegistrationState {
  const RegistrationState({
    this.status = RegStatus.loading,
    this.clientKey,
    this.error,
    this.errorAt,
  });

  final RegStatus status;
  final String? clientKey;

  /// Last heartbeat failure, set only once retries are exhausted (see
  /// RegistrationController._retryDelays). [errorAt] lets the UI distinguish a
  /// fresh error from a repeat of the same message, mirroring VpnConnectionState.
  final String? error;
  final DateTime? errorAt;

  RegistrationState copyWith({
    RegStatus? status,
    String? clientKey,
    String? error,
    DateTime? errorAt,
  }) =>
      RegistrationState(
        status: status ?? this.status,
        clientKey: clientKey ?? this.clientKey,
        error: error,
        errorAt: errorAt,
      );
}

/// Owns device identity + registration. Heartbeats, ingests delivered configs
/// into the [ConfigStore] (one-shot, so it must happen on each heartbeat), and
/// tracks activation status. The profile list lives in [ProfilesController].
class RegistrationController extends StateNotifier<RegistrationState> {
  RegistrationController({
    required this.baseUrl,
    required this.keyStore,
    required this.deviceInfo,
    required this.pushTokens,
    required this.configStore,
    required this.backendFactory,
    required this.onConfigsChanged,
    required this.onGatewayTargets,
    required this.onFallbackTargets,
    required this.onRemoteLanCidrs,
    required this.onMfaState,
    required this.onMfaGate,
    required this.onProfileDeleted,
    required this.onProfileChange,
    required this.onPendingApprovals,
    required this.onAutoConnect,
    this.pollInterval,
  }) : super(const RegistrationState());

  final String baseUrl;
  final KeyStore keyStore;
  final DeviceInfoSource deviceInfo;
  final PushTokens pushTokens;
  final ConfigStore configStore;
  final BackendApi Function(String, String) backendFactory;
  final void Function() onConfigsChanged;
  final void Function(Map<String, GatewayTarget>) onGatewayTargets;
  final void Function(Map<String, FallbackTarget>) onFallbackTargets;
  final void Function(Map<String, List<String>>) onRemoteLanCidrs;
  final void Function(MfaState) onMfaState;

  /// Called when a heartbeat reports the MFA gate is required again (session
  /// expired). Argument is the gated server profile name so the connection layer
  /// only tears down that tunnel, not a hand-connected foreign one.
  final void Function(String? gatedProfile) onMfaGate;

  /// Called after a backend-requested profile deletion has been applied to the
  /// config store, so the connection layer can tear down that tunnel if it's active.
  final void Function(String deletedProfile) onProfileDeleted;

  /// Called for every profile add/update/delete detected during config ingest,
  /// purely so the UI can surface a notification — does not drive any tunnel logic
  /// (that's [onProfileDeleted]).
  final void Function(ProfileChangeEvent) onProfileChange;
  final void Function(List<PendingApproval>) onPendingApprovals;

  /// Applies the backend's on-demand auto-connect policy. [configText] is the
  /// decrypted wg-quick for [profileName] (so the OS has a tunnel to bring up), or
  /// null when auto-connect is off / no config is stored.
  final void Function(bool enabled, String? profileName, String? configText)
      onAutoConnect;
  final Duration? pollInterval;

  BackendApi? _api;
  String? _hostname;
  Timer? _timer;
  Future<void>? _inFlightRefresh;
  bool _mfaPending = false;

  // A heartbeat right after the app resumes/launches can hit a radio that
  // hasn't reconnected yet (a few seconds of Wi-Fi/cellular reassociation is
  // normal right after unlocking a phone) — retry a few times with backoff
  // before surfacing a hard error, so a short blip resolves invisibly.
  static const _retryDelays = [
    Duration(seconds: 2),
    Duration(seconds: 4),
    Duration(seconds: 8),
  ];

  // The very first reachability check, right as the app opens, can race the
  // OS network stack before it's finished initializing (seen as a false
  // "backend unreachable" flash on launch even though the backend is fine).
  // Give it a few seconds to settle before probing for the first time.
  static const _initialCheckDelay = Duration(seconds: 5);

  // While an MFA gate is actively pending (enrollment open or authorization
  // required), poll much faster than the normal heartbeat interval — the user
  // is typically staring at the screen waiting, e.g. for another device to
  // approve a push-to-approve request, and a full 60 s wait to notice the
  // gate cleared reads as "stuck".
  static const _mfaPendingPollInterval = Duration(seconds: 5);

  /// Secure-storage key for the installation's client API key (see StatusResponse.clientApiKey).
  static const _apiKeyStorageKey = kClientApiKeyStorageKey;

  /// Reports a Connect/Disconnect/Verified event to the backend (mirrors the desktop/Linux/
  /// macOS clients' LogEventAsync/log_event/logEvent) — drives the admin client list's
  /// presence/connected indicators. Reuses the same [_api] instance `start()` built (already
  /// carries any adopted rotated API key) rather than constructing a fresh one. Best-effort:
  /// a no-op before `start()` has set [_api] (there's no clientKey to report against yet), and
  /// failures are swallowed — the underlying connect/disconnect/verify already succeeded or
  /// failed on its own regardless of whether this report lands.
  Future<void> logEvent(String eventType, String tunnelName, {String? detail}) async {
    final api = _api;
    if (api == null) return;
    try {
      await api.logEvent(eventType, tunnelName, detail: detail);
    } catch (e) {
      DiagnosticsLog.instance.add('logEvent($eventType, $tunnelName) failed: $e');
    }
  }

  Future<void> start() async {
    final identity = await ClientIdentity.ensure(keyStore);
    _hostname = await deviceInfo.deviceName();
    state = state.copyWith(clientKey: identity.clientKey);
    final api = backendFactory(baseUrl, identity.clientKey);
    // Adopt a previously-rotated API key (persisted) so it survives an app restart; a fresh
    // install has none and uses the built-in key.
    final storedApiKey = await keyStore.read(_apiKeyStorageKey);
    if (storedApiKey != null && storedApiKey.isNotEmpty) api.setApiKey(storedApiKey);
    _api = api;
    await Future<void>.delayed(_initialCheckDelay);
    await refresh();
    await _registerPushToken();
    _scheduleNextPoll();
  }

  // Re-heartbeat so activation flips, freshly-delivered configs arrive, and an
  // MFA gate clears without the user restarting the app. Self-reschedules
  // (rather than Timer.periodic) so the interval can shrink while MFA-gated
  // and grow back once it clears.
  void _scheduleNextPoll() {
    final interval = pollInterval;
    if (interval == null) return; // disabled (tests: no timer leaks)
    final delay = _mfaPending ? _mfaPendingPollInterval : interval;
    _timer = Timer(delay, () async {
      await refresh();
      _scheduleNextPoll();
    });
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  /// Redeem a pairing code (from a scanned QR): activates this device + joins a
  /// customer, then refreshes so the active state + configs flow in.
  Future<void> pair(String token) async {
    final api = _api;
    if (api == null) return;
    // Pairing returns the installation's API key — adopt + persist it so all later calls (and the
    // next app launch) authenticate. A keyless device would otherwise stay unauthenticated.
    final key = await api.redeemPairing(token, hostname: _hostname);
    if (key != null && key.isNotEmpty) {
      api.setApiKey(key);
      await keyStore.write(_apiKeyStorageKey, key);
    }
    await refresh();
  }

  /// Best-effort: fetch the FCM token and register it for push-to-approve.
  /// Failures (no Firebase, endpoint absent on an older backend) are ignored.
  Future<void> _registerPushToken() async {
    final api = _api;
    if (api == null) return;
    try {
      final token = await pushTokens.token();
      if (token != null && token.isNotEmpty) {
        await api.registerDevice(token);
      }
    } catch (_) {
      // push optional — never block registration on it
    }
  }

  /// Approve a cross-device challenge (after number-match + biometric on the UI).
  Future<bool> approveChallenge(int challengeId, int matchedNumber) async {
    final api = _api;
    if (api == null) return false;
    try {
      await api.approveMfa(challengeId, matchedNumber);
      await refresh();
      return true;
    } catch (_) {
      await refresh();
      return false;
    }
  }

  Future<void> denyChallenge(int challengeId) async {
    final api = _api;
    if (api == null) return;
    try {
      await api.denyMfa(challengeId);
    } catch (_) {
      // best-effort
    }
    await refresh();
  }

  /// Confirm TOTP enrollment with a 6-digit code; refreshes on success.
  Future<bool> confirmEnrollment(String code) async {
    final api = _api;
    if (api == null) return false;
    final ok = await api.confirmMfaEnrollment(code);
    if (ok) await refresh();
    return ok;
  }

  /// Heartbeats the backend, retrying transient failures with backoff before
  /// surfacing an error (see [_retryDelays]). Concurrent callers (resume
  /// trigger + periodic timer racing, say) share the same in-flight attempt
  /// rather than firing duplicate heartbeats.
  Future<void> refresh() {
    final existing = _inFlightRefresh;
    if (existing != null) return existing;
    final future = _refreshWithRetry().whenComplete(() => _inFlightRefresh = null);
    _inFlightRefresh = future;
    return future;
  }

  Future<void> _refreshWithRetry() async {
    final api = _api;
    if (api == null) return;
    for (var attempt = 0; ; attempt++) {
      try {
        final knownProfiles =
            (await configStore.list()).map((p) => p.name).toList();
        final resp = await api.register(
          trayRunning: true,
          hostname: _hostname,
          profiles: knownProfiles,
        );
        // Backend key rotation: adopt + persist the new key so subsequent requests (and the
        // next app launch) authenticate with it. Only sent while still on the previous key.
        final rotatedKey = resp.clientApiKey;
        if (rotatedKey != null && rotatedKey.isNotEmpty) {
          api.setApiKey(rotatedKey);
          await keyStore.write(_apiKeyStorageKey, rotatedKey);
        }
        await _ingestConfigs(api, resp);
        onGatewayTargets({
          for (final g in resp.gatewayProfiles)
            if (g.gatewayIp.isNotEmpty && g.healthPort > 0)
              g.profileName:
                  GatewayTarget(ip: g.gatewayIp, healthPort: g.healthPort),
        });
        onFallbackTargets({
          for (final f in resp.fallbackEndpoints)
            if (f.profileName.isNotEmpty && f.port > 0)
              f.profileName: FallbackTarget(
                  port: f.port, triggerSeconds: f.triggerSeconds),
        });
        onRemoteLanCidrs({
          for (final e in resp.remoteLanCidrsByProfile)
            if (e.profileName.isNotEmpty && e.cidrs.isNotEmpty) e.profileName: e.cidrs,
        });
        _mfaPending = resp.mfaRequired || resp.mfaEnrollmentOpen;
        onMfaState(MfaState(
          required: resp.mfaRequired,
          authorizeUrl: resp.mfaAuthorizeUrl,
          enrollmentOpen: resp.mfaEnrollmentOpen,
          enrollmentUri: resp.mfaEnrollmentUri,
          sessionExpiresAt: resp.mfaSessionExpiresAt,
        ));
        // Gate returned (session expired): tear down the now-dead gated tunnel so the
        // client doesn't linger showing "connected" (mirrors the Windows/Linux clients).
        if (resp.mfaRequired) onMfaGate(resp.serverProfileName);
        onPendingApprovals(resp.pendingApprovals);
        // On-demand auto-connect (iOS): mirror the backend flag, but never while an
        // MFA gate is up (an auto-connect to a gated peer can't verify). Read the
        // target config so the OS has a tunnel to bring up.
        final autoOn = resp.autoConnectEnabled && !resp.mfaRequired;
        final acName = resp.autoConnectProfileName;
        final acConfig = (autoOn && acName != null && acName.isNotEmpty)
            ? await configStore.read(acName)
            : null;
        onAutoConnect(autoOn, acName, acConfig);
        state = state.copyWith(
          status: resp.duplicatePending
              ? RegStatus.duplicate
              : resp.isActive
                  ? RegStatus.active
                  : RegStatus.awaitingActivation,
        );
        DiagnosticsLog.instance.add(
            'heartbeat ok: active=${resp.isActive} mfaRequired=${resp.mfaRequired}');
        // Admin requested a diagnostic log upload — send the (app-only, redacted) bundle.
        if (resp.logUploadRequested) unawaited(_uploadLogs('Admin'));
        return;
      } catch (e) {
        if (attempt >= _retryDelays.length) {
          DiagnosticsLog.instance.add('heartbeat error: $e');
          state = state.copyWith(
            status: RegStatus.error,
            error: e.toString(),
            errorAt: DateTime.now(),
          );
          return;
        }
        await Future<void>.delayed(_retryDelays[attempt]);
      }
    }
  }

  /// Sends the (app-only, redacted) diagnostic bundle. Public entry used by the
  /// app's "Send logs" menu action; the admin-pull path calls it with "Admin".
  Future<void> sendLogs() => _uploadLogs('User');

  Future<void> _uploadLogs(String trigger) async {
    final api = _api;
    if (api == null) return;
    try {
      final gz = DiagnosticsLog.instance.buildGzippedBundle();
      await api.uploadLogs(gz, trigger);
    } catch (_) {
      // best-effort; diagnostics upload never disrupts the app
    }
  }

  /// Persist any configs the heartbeat delivered, and remove any it asked deleted.
  /// Foreign configs are inline and one-shot; a native pending config is fetched
  /// (and cleared) via GET. Both are cleared backend-side on delivery, so this must
  /// run every heartbeat. The delete is a one-shot request too — the backend only
  /// clears `pendingDeleteProfileName` once a later heartbeat's `profiles` list
  /// (sent from [_refreshWithRetry]) confirms the name is actually gone, so an
  /// interrupted delete here safely retries on the next heartbeat. Every add/update/
  /// delete also fires [onProfileChange] so the UI can show a one-shot notification.
  Future<void> _ingestConfigs(BackendApi api, StatusResponse resp) async {
    var changed = false;
    // Snapshot before any writes below, so a config that's both added and later
    // re-provisioned within the same batch still classifies against what the
    // device actually had before this heartbeat started.
    final existingNames =
        (await configStore.list()).map((p) => p.name).toSet();

    for (final c in resp.pendingForeignConfigs) {
      if (await _store(c.fileName, c.content, existingNames)) changed = true;
    }

    if (resp.hasPendingConfig) {
      final pc = await api.fetchPendingConfig();
      if (pc != null && await _store(pc.fileName, pc.content, existingNames)) {
        changed = true;
      }
    }

    final deleteName = resp.pendingDeleteProfileName;
    if (deleteName != null && deleteName.isNotEmpty) {
      await configStore.delete(deleteName);
      changed = true;
      onProfileDeleted(deleteName);
      onProfileChange(ProfileChangeEvent(deleteName, ProfileChangeKind.deleted));
    }

    if (changed) onConfigsChanged();
  }

  Future<bool> _store(
    String fileName,
    String content,
    Set<String> existingNames,
  ) async {
    final name = profileNameFromFileName(fileName);
    if (!isValidProfileName(name) || content.isEmpty) return false;
    await configStore.save(name, content);
    onProfileChange(ProfileChangeEvent(
      name,
      existingNames.contains(name)
          ? ProfileChangeKind.updated
          : ProfileChangeKind.added,
    ));
    return true;
  }
}
