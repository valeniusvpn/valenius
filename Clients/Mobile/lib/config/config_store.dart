import 'dart:convert';

import '../platform/key_store.dart';
import 'profile_name.dart';

class StoredProfile {
  const StoredProfile({required this.name, this.deletable = false});

  final String name;

  /// True for user-uploaded profiles (deletable by the user); admin/sidecar
  /// delivered profiles are managed (false).
  final bool deletable;

  Map<String, dynamic> toJson() => {'name': name, 'deletable': deletable};

  factory StoredProfile.fromJson(Map<String, dynamic> j) => StoredProfile(
        name: j['name'] as String,
        deletable: (j['deletable'] as bool?) ?? false,
      );
}

abstract interface class ConfigStore {
  Future<List<StoredProfile>> list();

  /// Decrypted wg-quick text for [name], or null if absent.
  Future<String?> read(String name);

  Future<void> save(String name, String config, {bool deletable = false});

  Future<void> delete(String name);
}

/// Keystore-backed store: each config lives in secure storage (encrypted at rest,
/// the mobile analogue of the desktop `.conf.enc`); a JSON index entry lists the
/// profiles. Decrypted text is only ever held in memory.
class SecureConfigStore implements ConfigStore {
  SecureConfigStore(this._keys);

  final KeyStore _keys;

  static const _indexKey = 'valenius.profiles';
  static String _configKey(String name) => 'valenius.config.$name';

  @override
  Future<List<StoredProfile>> list() async {
    final raw = await _keys.read(_indexKey);
    if (raw == null || raw.isEmpty) return const [];
    final decoded = jsonDecode(raw) as List<dynamic>;
    return decoded
        .map((e) => StoredProfile.fromJson((e as Map).cast<String, dynamic>()))
        .toList();
  }

  @override
  Future<String?> read(String name) => _keys.read(_configKey(name));

  @override
  Future<void> save(String name, String config, {bool deletable = false}) async {
    if (!isValidProfileName(name)) {
      throw ArgumentError('Invalid profile name: $name');
    }
    await _keys.write(_configKey(name), config);
    final next = [
      for (final p in await list())
        if (p.name != name) p,
      StoredProfile(name: name, deletable: deletable),
    ]..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    await _writeIndex(next);
  }

  @override
  Future<void> delete(String name) async {
    await _keys.delete(_configKey(name));
    final next = [
      for (final p in await list())
        if (p.name != name) p,
    ];
    await _writeIndex(next);
  }

  Future<void> _writeIndex(List<StoredProfile> profiles) =>
      _keys.write(_indexKey, jsonEncode([for (final p in profiles) p.toJson()]));
}
