import 'dart:math';

import '../platform/key_store.dart';

/// The persistent device identity. `ClientKey` is a v4 GUID generated on first
/// run and kept in the secret store — the mobile analogue of the desktop
/// `registration.json` ClientKey. Clearing app data yields a new identity that
/// needs re-activation.
class ClientIdentity {
  const ClientIdentity(this.clientKey);

  final String clientKey;

  static const _keyName = 'valenius.clientKey';

  // Two call sites resolve identity independently on first run — the QR pairing
  // flow's one-off client (setup_qr_screen.dart) and RegistrationController.start(),
  // which fires as soon as the URL it just configured flips HomeScreen on. Without
  // this cache, both see no stored key (read-then-write below has no atomicity of
  // its own) and each mints its own GUID, so pairing and the first heartbeat send
  // two different ClientKeys — two backend Client rows for one new device. Keyed by
  // store instance (not a bare static Future) so tests using a fresh KeyStore per
  // test stay isolated from each other.
  static final Map<KeyStore, Future<ClientIdentity>> _inFlight = {};

  static Future<ClientIdentity> ensure(KeyStore store) =>
      _inFlight[store] ??= _resolve(store);

  static Future<ClientIdentity> _resolve(KeyStore store) async {
    final existing = await store.read(_keyName);
    if (existing != null && existing.isNotEmpty) {
      return ClientIdentity(existing);
    }
    final key = _newGuidV4();
    await store.write(_keyName, key);
    return ClientIdentity(key);
  }

  static String _newGuidV4() {
    final r = Random.secure();
    final b = List<int>.generate(16, (_) => r.nextInt(256));
    b[6] = (b[6] & 0x0f) | 0x40; // version 4
    b[8] = (b[8] & 0x3f) | 0x80; // RFC 4122 variant
    final hex = b.map((x) => x.toRadixString(16).padLeft(2, '0')).join();
    return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
        '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
  }
}
