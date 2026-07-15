import 'dart:async';
import 'dart:convert';
import 'dart:io' show Platform;

import 'package:http/http.dart' as http;

import 'models.dart';

/// Reported to the backend as `Client.Platform` so the admin panel can tell
/// devices apart. One shared Dart codebase runs on both, so resolve it at runtime.
String get _clientPlatform => Platform.isIOS ? 'iOS' : 'Android';

/// The backend surface the app depends on. An interface so tests inject a fake
/// without HTTP.
abstract interface class BackendApi {
  /// POST /api/clients/register — heartbeat; returns the current status.
  Future<StatusResponse> register({required bool trayRunning, String? hostname});

  /// POST /api/clients/pairing/redeem — activate + join a customer via a QR code. Returns the
  /// installation's API key (the caller persists it), or null if the backend didn't send one.
  Future<String?> redeemPairing(String token, {String? hostname});

  /// GET /api/clients/pending-config — one-shot fetch of a staged config;
  /// null when none is pending. (The backend clears it on read.)
  Future<PendingConfig?> fetchPendingConfig();

  /// POST /api/clients/device — register the FCM token for push-to-approve.
  Future<void> registerDevice(String fcmToken);

  /// POST /api/clients/mfa/approve — approve a cross-device challenge (number-match).
  Future<void> approveMfa(int challengeId, int matchedNumber);

  /// POST /api/clients/mfa/deny — deny a cross-device challenge.
  Future<void> denyMfa(int challengeId);

  /// POST /api/clients/mfa/enroll-confirm — confirm TOTP enrollment with a code.
  Future<bool> confirmMfaEnrollment(String code);

  /// POST /api/clients/offline — mark this client offline (on background/exit).
  Future<void> reportOffline();

  /// GET /api/version — side-effect-free reachability probe for the "Backend check"
  /// menu item (the same public endpoint the Windows tray's About check uses).
  Future<BackendCheckResult> checkBackend();

  /// POST /api/clients/logs — upload a gzipped, redacted diagnostic bundle.
  /// [trigger] is "Admin" (heartbeat request) or "User" (sent from the app).
  Future<bool> uploadLogs(List<int> gzBytes, String trigger);

  /// Adopt a backend-rotated API key for all subsequent requests (see
  /// StatusResponse.clientApiKey). The caller persists it to secure storage.
  void setApiKey(String apiKey);

  void close();
}

/// Outcome of a manual "Backend check". [reachable] is true only when the backend
/// answered a ping successfully with an accepted API key.
class BackendCheckResult {
  const BackendCheckResult({required this.reachable, required this.detail});

  final bool reachable;
  final String detail;
}

/// HTTP implementation talking to the same backend as the desktop/Linux clients.
///
/// The app ships with **no** API key — there is deliberately no shared, published key. Each backend
/// installation has its own key (auto-generated per install); the device obtains it at onboarding:
/// QR pairing returns it (see [redeemPairing]), and a keyless first-run device registers as *pending*
/// and adopts the key once an admin activates it (delivered via `StatusResponse.clientApiKey`). Until
/// then `apiKey` is empty and keyed endpoints reject the device, which is expected pre-activation.
class BackendClient implements BackendApi {
  BackendClient({
    required this.baseUrl,
    required this.clientKey,
    this.apiKey = '',
    http.Client? httpClient,
  }) : _http = httpClient ?? http.Client();

  final String baseUrl;
  final String clientKey;
  String apiKey; // mutable so a backend key rotation takes effect without rebuilding the client
  final http.Client _http;

  @override
  void setApiKey(String apiKey) => this.apiKey = apiKey;

  // Bounds how long a single attempt can hang before RegistrationController's
  // retry loop moves on — without this, a request issued while the radio is
  // still reconnecting (e.g. right after unlocking the phone) can sit for far
  // longer than the OS's own connect timeout before failing.
  static const _timeout = Duration(seconds: 8);

  Map<String, String> get _jsonHeaders =>
      {'Content-Type': 'application/json', 'X-Api-Key': apiKey};

  @override
  Future<StatusResponse> register({
    required bool trayRunning,
    String? hostname,
  }) async {
    final res = await _http.post(
      Uri.parse('$baseUrl/api/clients/register'),
      headers: _jsonHeaders,
      // HostSid/Username/UserSid are non-nullable on the backend RegisterRequest,
      // so [ApiController] treats them as required — send empty strings (mobile
      // has no Windows SID/user concept) or the endpoint returns HTTP 400.
      body: jsonEncode({
        'clientKey': clientKey,
        'hostname': hostname ?? '',
        'hostSid': '',
        'username': '',
        'userSid': '',
        'trayRunning': trayRunning,
        'platform': _clientPlatform,
      }),
    ).timeout(_timeout);
    if (res.statusCode != 200) {
      throw BackendException('register failed: HTTP ${res.statusCode}');
    }
    return StatusResponse.fromJson(
      jsonDecode(res.body) as Map<String, dynamic>,
    );
  }

  @override
  Future<String?> redeemPairing(String token, {String? hostname}) async {
    final res = await _http.post(
      Uri.parse('$baseUrl/api/clients/pairing/redeem'),
      headers: _jsonHeaders,
      body: jsonEncode({
        'token': token,
        'clientKey': clientKey,
        'hostname': hostname ?? '',
        'platform': _clientPlatform,
      }),
    ).timeout(_timeout);
    if (res.statusCode != 200) {
      throw BackendException('pairing failed: HTTP ${res.statusCode}');
    }
    // The backend returns the installation's API key so a keyless device adopts it.
    final body = jsonDecode(res.body);
    final key = (body is Map && body['apiKey'] is String) ? body['apiKey'] as String : null;
    return (key != null && key.isNotEmpty) ? key : null;
  }

  @override
  Future<PendingConfig?> fetchPendingConfig() async {
    final res = await _http.get(
      Uri.parse('$baseUrl/api/clients/pending-config'
          '?clientKey=${Uri.encodeQueryComponent(clientKey)}'),
      headers: {'X-Api-Key': apiKey},
    ).timeout(_timeout);
    if (res.statusCode == 204 || res.body.isEmpty) return null;
    if (res.statusCode != 200) {
      throw BackendException('pending-config failed: HTTP ${res.statusCode}');
    }
    return PendingConfig.fromJson(jsonDecode(res.body) as Map<String, dynamic>);
  }

  @override
  Future<void> registerDevice(String fcmToken) async {
    final res = await _http.post(
      Uri.parse('$baseUrl/api/clients/device'),
      headers: _jsonHeaders,
      body: jsonEncode({'clientKey': clientKey, 'fcmToken': fcmToken}),
    ).timeout(_timeout);
    if (res.statusCode != 200) {
      throw BackendException('device register failed: HTTP ${res.statusCode}');
    }
  }

  @override
  Future<void> approveMfa(int challengeId, int matchedNumber) async {
    final res = await _http.post(
      Uri.parse('$baseUrl/api/clients/mfa/approve'),
      headers: _jsonHeaders,
      body: jsonEncode({
        'clientKey': clientKey,
        'challengeId': challengeId,
        'matchedNumber': matchedNumber,
      }),
    ).timeout(_timeout);
    if (res.statusCode != 200) {
      throw BackendException('approve failed: HTTP ${res.statusCode}');
    }
  }

  @override
  Future<void> denyMfa(int challengeId) async {
    await _http.post(
      Uri.parse('$baseUrl/api/clients/mfa/deny'),
      headers: _jsonHeaders,
      body: jsonEncode({'clientKey': clientKey, 'challengeId': challengeId}),
    ).timeout(_timeout);
  }

  @override
  Future<bool> confirmMfaEnrollment(String code) async {
    final res = await _http.post(
      Uri.parse('$baseUrl/api/clients/mfa/enroll-confirm'),
      headers: _jsonHeaders,
      body: jsonEncode({'clientKey': clientKey, 'code': code}),
    ).timeout(_timeout);
    return res.statusCode == 200;
  }

  @override
  Future<void> reportOffline() async {
    await _http.post(
      Uri.parse('$baseUrl/api/clients/offline'),
      headers: _jsonHeaders,
      body: jsonEncode({'clientKey': clientKey}),
    ).timeout(_timeout);
  }

  @override
  Future<BackendCheckResult> checkBackend() async {
    // Mirrors the Windows tray's About-dialog check: any 2xx from the public
    // /api/version endpoint means the backend is reachable. No API key required.
    try {
      final res = await _http
          .get(Uri.parse('$baseUrl/api/version'))
          .timeout(_timeout);
      if (res.statusCode >= 200 && res.statusCode < 300) {
        return const BackendCheckResult(
            reachable: true, detail: 'Backend reachable.');
      }
      return BackendCheckResult(
          reachable: false, detail: 'Backend returned HTTP ${res.statusCode}.');
    } on TimeoutException {
      return const BackendCheckResult(
          reachable: false, detail: 'Timed out reaching the backend.');
    } catch (e) {
      return BackendCheckResult(
          reachable: false, detail: 'Could not reach the backend: $e');
    }
  }

  @override
  Future<bool> uploadLogs(List<int> gzBytes, String trigger) async {
    try {
      final req = http.MultipartRequest(
          'POST', Uri.parse('$baseUrl/api/clients/logs'))
        ..headers['X-Api-Key'] = apiKey
        ..fields['clientKey'] = clientKey
        ..fields['trigger'] = trigger
        ..files.add(http.MultipartFile.fromBytes('file', gzBytes,
            filename: 'valenius-logs.log.gz'));
      final res = await req.send().timeout(_timeout);
      return res.statusCode == 200;
    } catch (_) {
      return false;
    }
  }

  // TODO(milestone 4): GET /api/clients/pending-config + foreign config receipt.

  @override
  void close() => _http.close();
}

class BackendException implements Exception {
  BackendException(this.message);
  final String message;
  @override
  String toString() => 'BackendException: $message';
}
