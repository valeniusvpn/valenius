import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Platform-neutral secret store. Behind an interface so the rest of the app
/// never touches Keystore/Keychain directly (and tests use an in-memory fake).
abstract interface class KeyStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

/// Android Keystore-backed implementation (encrypted shared prefs).
class SecureKeyStore implements KeyStore {
  SecureKeyStore()
      : _storage = const FlutterSecureStorage(
          aOptions: AndroidOptions(encryptedSharedPreferences: true),
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
