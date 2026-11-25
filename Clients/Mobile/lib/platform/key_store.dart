import 'dart:io';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:valenius_vpn/valenius_vpn.dart';

import '../diagnostics/diagnostics.dart';

/// Platform-neutral secret store. Behind an interface so the rest of the app
/// never touches Keystore/Keychain directly (and tests use an in-memory fake).
abstract interface class KeyStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

/// Thrown by [SecureKeyStore] while the one-time v9 → v11 handover can't run
/// (the old store exists but couldn't be decrypted this launch). Every read and
/// write fails for the rest of the app session instead of returning "nothing
/// stored" — returning null would make [ClientIdentity] mint a fresh ClientKey and
/// orphan the device's real identity + credential. Retried on the next launch.
class SecureStorageUnavailableException implements Exception {
  const SecureStorageUnavailableException(this.message);
  final String message;
  @override
  String toString() => 'SecureStorageUnavailableException: $message';
}

/// The raw secret backend [SecureKeyStore] writes to (flutter_secure_storage v11 in
/// production; a map in tests).
abstract interface class SecretBackend {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

/// Snapshot of the pre-v11 store (see [LegacyStoreSource]).
class LegacyStoreSnapshot {
  const LegacyStoreSnapshot({
    required this.status,
    this.entries = const {},
    this.skipped = 0,
    this.error,
  });

  factory LegacyStoreSnapshot.fromMap(Map<String, dynamic> m) =>
      LegacyStoreSnapshot(
        status: m['status'] as String? ?? 'failed',
        entries: (m['entries'] as Map?)?.cast<String, String>() ?? const {},
        skipped: (m['skipped'] as num?)?.toInt() ?? 0,
        error: m['error'] as String?,
      );

  /// none | ok | failed | abandoned (see LegacySecureStorage.kt).
  final String status;
  final Map<String, String> entries;

  /// v9 plain-fallback entries that can't be migrated (diagnostics only).
  final int skipped;
  final String? error;
}

/// Reads / removes the flutter_secure_storage v9 data.
abstract interface class LegacyStoreSource {
  Future<LegacyStoreSnapshot> read();
  Future<void> delete();
}

/// Production [LegacyStoreSource]: the native reader in the valenius_vpn plugin on
/// Android. iOS needs none — its Keychain items are read by v11 unchanged (same
/// default service name), so it always reports `none`.
class PlatformLegacyStoreSource implements LegacyStoreSource {
  const PlatformLegacyStoreSource();

  @override
  Future<LegacyStoreSnapshot> read() async {
    if (!Platform.isAndroid) return const LegacyStoreSnapshot(status: 'none');
    return LegacyStoreSnapshot.fromMap(await ValeniusLegacyStorage().read());
  }

  @override
  Future<void> delete() async {
    if (Platform.isAndroid) await ValeniusLegacyStorage().delete();
  }
}

class _FlutterSecureStorageBackend implements SecretBackend {
  _FlutterSecureStorageBackend()
      : _storage = const FlutterSecureStorage(
          aOptions: AndroidOptions(
            // Own namespace: v11 never opens the v9 "FlutterSecureStorage" file (which it
            // can't decrypt), so there's nothing for it to "recover" or wipe there.
            storageNamespace: 'valenius',
            // Never silently delete secrets on a decrypt error (v11 defaults to true).
            // A wiped store means a new ClientKey and a lost device credential; an error
            // surfaces and the next launch retries instead.
            resetOnError: false,
          ),
        );

  final FlutterSecureStorage _storage;

  @override
  Future<String?> read(String key) => _storage.read(key: key);
  @override
  Future<void> write(String key, String value) =>
      _storage.write(key: key, value: value);
  @override
  Future<void> delete(String key) => _storage.delete(key: key);
}

/// Keystore/Keychain-backed implementation. On first use per app launch it makes
/// sure the flutter_secure_storage v9 data (if any) has been handed over to the v11
/// store — before any secret is read or written. See [migrateLegacyStore].
class SecureKeyStore implements KeyStore {
  SecureKeyStore({
    SecretBackend? backend,
    LegacyStoreSource? legacy,
    void Function(String)? log,
  })  : _backend = backend ?? _FlutterSecureStorageBackend(),
        _legacy = legacy ?? const PlatformLegacyStoreSource(),
        _log = log ?? DiagnosticsLog.instance.add;

  final SecretBackend _backend;
  final LegacyStoreSource _legacy;
  final void Function(String) _log;

  // Deliberately memoized even when it fails: a failed handover keeps the store
  // unavailable for this session (see SecureStorageUnavailableException) and is
  // retried on the next launch — the native side counts launches, not calls.
  Future<void>? _ready;

  Future<void> _ensureReady() =>
      _ready ??= migrateLegacyStore(_backend, _legacy, _log);

  @override
  Future<String?> read(String key) async {
    await _ensureReady();
    return _backend.read(key);
  }

  @override
  Future<void> write(String key, String value) async {
    await _ensureReady();
    await _backend.write(key, value);
  }

  @override
  Future<void> delete(String key) async {
    await _ensureReady();
    await _backend.delete(key);
  }
}

/// Marker in the v11 store: the v9 handover has been dealt with on this device.
const legacyMigrationMarkerKey = 'valenius.secureStorageV9Handover';

/// Copies every flutter_secure_storage v9 entry into the v11 store exactly once.
///
/// Order matters for crash safety: copy → read back and compare → marker → delete
/// the v9 file. A crash anywhere before the marker just repeats the (idempotent)
/// copy next launch; a value already present in the v11 store is never overwritten.
Future<void> migrateLegacyStore(
  SecretBackend backend,
  LegacyStoreSource legacy,
  void Function(String) log,
) async {
  if (await backend.read(legacyMigrationMarkerKey) != null) return;

  final snap = await legacy.read();
  switch (snap.status) {
    case 'none':
      if (snap.skipped > 0) {
        log('Secure storage: ${snap.skipped} legacy plain-fallback entries '
            'could not be migrated');
      }
      await backend.write(legacyMigrationMarkerKey, 'none');
      return;
    case 'ok':
      var copied = 0;
      for (final e in snap.entries.entries) {
        if (await backend.read(e.key) != null) continue;
        await backend.write(e.key, e.value);
        if (await backend.read(e.key) != e.value) {
          throw SecureStorageUnavailableException(
              'verification failed after copying "${e.key}"');
        }
        copied++;
      }
      await backend.write(legacyMigrationMarkerKey, 'migrated');
      log('Secure storage: migrated $copied of ${snap.entries.length} entries '
          'from the previous storage format'
          '${snap.skipped > 0 ? ' (${snap.skipped} unreadable skipped)' : ''}');
      try {
        await legacy.delete();
      } catch (e) {
        // Harmless leftover: the marker stops it from ever being read again.
        log('Secure storage: could not remove the old store: $e');
      }
      return;
    case 'abandoned':
      // Undecryptable on this device for several launches running (typically a
      // backup restored onto a new phone, where the old Keystore key doesn't exist)
      // — the same situation v9 itself treated as a fresh install. Start fresh.
      log('Secure storage: previous data unreadable on this device, starting '
          'fresh (${snap.error})');
      await backend.write(legacyMigrationMarkerKey, 'abandoned');
      return;
    default:
      log('Secure storage: previous data not readable yet, will retry on next '
          'launch (${snap.error})');
      throw SecureStorageUnavailableException(
          snap.error ?? 'previous secure storage could not be read');
  }
}
