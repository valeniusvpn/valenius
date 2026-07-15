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

  static Future<ClientIdentity> ensure(KeyStore store) async {
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
