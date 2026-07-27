import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:valenius_mobile/api/backend_client.dart';
import 'package:valenius_mobile/api/models.dart';
import 'package:valenius_mobile/app/app.dart';
import 'package:valenius_mobile/config/config_store.dart';
import 'package:valenius_mobile/platform/device_info.dart';
import 'package:valenius_mobile/platform/key_store.dart';
import 'package:valenius_mobile/platform/push.dart';
import 'package:valenius_mobile/state/app_state.dart';
import 'package:valenius_mobile/state/registration.dart';

class _MemKeyStore implements KeyStore {
  final _store = <String, String>{};
  @override
  Future<String?> read(String key) async => _store[key];
  @override
  Future<void> write(String key, String value) async => _store[key] = value;
  @override
  Future<void> delete(String key) async => _store.remove(key);
}

class _FakeDeviceInfo implements DeviceInfoSource {
  @override
  Future<String> deviceName() async => 'Test Device';
}

class _FakePushTokens implements PushTokens {
  @override
  Future<String?> token() async => null;
}

class _FakeConfigStore implements ConfigStore {
  @override
  Future<List<StoredProfile>> list() async => const [
        StoredProfile(name: 'Server-VPN'),
        StoredProfile(name: 'HomeNet-VPN', deletable: true),
      ];
  @override
  Future<String?> read(String name) async => '[Interface]\n';
  @override
  Future<void> save(String name, String config, {bool deletable = false}) async {}
  @override
  Future<void> delete(String name) async {}
}

/// Active registration. No pending configs/deletes by default; tests that
/// exercise the profile-change notification pass one in.
class _FakeApi implements BackendApi {
  _FakeApi({
    this.pendingForeignConfigs = const [],
    this.pendingDeleteProfileName,
  });

  final List<PendingConfig> pendingForeignConfigs;
  final String? pendingDeleteProfileName;

  @override
  Future<StatusResponse> register({
    required bool trayRunning,
    required List<String> profiles,
    String? hostname,
  }) async =>
      StatusResponse(
        isActive: true,
        hasPendingConfig: false,
        autoConnectEnabled: false,
        autoConnectProfileName: null,
        serverProfileName: 'Server-VPN',
        serverVpnIp: null,
        pendingConnect: false,
        pendingDisconnect: false,
        duplicatePending: false,
        mfaRequired: false,
        mfaAuthorizeUrl: null,
        mfaEnrollmentUri: null,
        mfaEnrollmentOpen: false,
        mfaSessionExpiresAt: null,
        gatewayProfiles: const [],
        pendingForeignConfigs: pendingForeignConfigs,
        mfaApproveNumber: null,
        pendingApprovals: const [],
        logUploadRequested: false,
        remoteLanCidrsByProfile: const [],
        pendingDeleteProfileName: pendingDeleteProfileName,
      );

  @override
  Future<void> registerDevice(String fcmToken) async {}

  @override
  Future<void> approveMfa(int challengeId, int matchedNumber) async {}

  @override
  Future<void> denyMfa(int challengeId) async {}

  @override
  void setApiKey(String apiKey) {}

  @override
  Future<String?> redeemPairing(String token, {String? hostname}) async => null;

  @override
  Future<bool> confirmMfaEnrollment(String code) async => true;

  @override
  Future<PendingConfig?> fetchPendingConfig() async => null;

  @override
  Future<void> reportOffline() async {}

  @override
  Future<void> logEvent(String eventType, String tunnelName) async {}

  @override
  Future<BackendCheckResult> checkBackend() async =>
      const BackendCheckResult(reachable: true, detail: 'Backend reachable.');

  @override
  Future<bool> uploadLogs(List<int> gzBytes, String trigger) async => true;

  @override
  void close() {}
}

void main() {
  testWidgets('renders the config-store profile list and toggles a connection',
      (WidgetTester tester) async {
    // The app now gates on a configured backend URL — seed one so the root gate
    // shows the home screen instead of the first-run setup screen.
    final keyStore = _MemKeyStore();
    await keyStore.write('valenius.backendUrl', 'https://test.example');
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          keyStoreProvider.overrideWithValue(keyStore),
          deviceInfoProvider.overrideWithValue(_FakeDeviceInfo()),
          pushTokensProvider.overrideWithValue(_FakePushTokens()),
          configStoreProvider.overrideWithValue(_FakeConfigStore()),
          backendFactoryProvider.overrideWithValue((_, __) => _FakeApi()),
          registrationPollIntervalProvider.overrideWithValue(null),
          // No real connectivity stream in tests (no platform channels).
          connectivityChangesProvider.overrideWithValue(null),
        ],
        child: const ValeniusApp(),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Server-VPN'), findsOneWidget);
    expect(find.text('HomeNet-VPN'), findsOneWidget);

    // Tapping a row connects it (stub tunnel resolves after a short delay).
    await tester.tap(find.text('Server-VPN'));
    await tester.pumpAndSettle(const Duration(seconds: 1));

    // Verification flips the row to the "Verified" pill (stub reports a handshake).
    expect(find.text('Verified'), findsOneWidget);
  });

  testWidgets('shows a notification when the heartbeat delivers a new profile',
      (WidgetTester tester) async {
    final keyStore = _MemKeyStore();
    await keyStore.write('valenius.backendUrl', 'https://test.example');
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          keyStoreProvider.overrideWithValue(keyStore),
          deviceInfoProvider.overrideWithValue(_FakeDeviceInfo()),
          pushTokensProvider.overrideWithValue(_FakePushTokens()),
          configStoreProvider.overrideWithValue(_FakeConfigStore()),
          backendFactoryProvider.overrideWithValue((_, __) => _FakeApi(
                pendingForeignConfigs: [
                  PendingConfig(
                      fileName: 'NewCo-VPN.conf', content: '[Interface]\n'),
                ],
              )),
          registrationPollIntervalProvider.overrideWithValue(null),
          connectivityChangesProvider.overrideWithValue(null),
        ],
        child: const ValeniusApp(),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text("Profile 'NewCo-VPN' added"), findsOneWidget);
  });

  testWidgets('shows a notification when the heartbeat deletes a profile',
      (WidgetTester tester) async {
    final keyStore = _MemKeyStore();
    await keyStore.write('valenius.backendUrl', 'https://test.example');
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          keyStoreProvider.overrideWithValue(keyStore),
          deviceInfoProvider.overrideWithValue(_FakeDeviceInfo()),
          pushTokensProvider.overrideWithValue(_FakePushTokens()),
          configStoreProvider.overrideWithValue(_FakeConfigStore()),
          backendFactoryProvider.overrideWithValue((_, __) =>
              _FakeApi(pendingDeleteProfileName: 'HomeNet-VPN')),
          registrationPollIntervalProvider.overrideWithValue(null),
          connectivityChangesProvider.overrideWithValue(null),
        ],
        child: const ValeniusApp(),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text("Profile 'HomeNet-VPN' removed"), findsOneWidget);
  });
}
