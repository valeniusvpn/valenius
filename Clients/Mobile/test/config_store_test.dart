import 'package:flutter_test/flutter_test.dart';
import 'package:valenius_mobile/config/config_store.dart';
import 'package:valenius_mobile/config/profile_name.dart';
import 'package:valenius_mobile/platform/key_store.dart';

class _MemKeyStore implements KeyStore {
  final store = <String, String>{};
  @override
  Future<String?> read(String key) async => store[key];
  @override
  Future<void> write(String key, String value) async => store[key] = value;
  @override
  Future<void> delete(String key) async => store.remove(key);
}

void main() {
  test('profile name validation matches the cross-platform rule', () {
    expect(isValidProfileName('Server-VPN'), isTrue);
    expect(isValidProfileName('a_b-1'), isTrue);
    expect(isValidProfileName(''), isFalse);
    expect(isValidProfileName('has space'), isFalse);
    expect(isValidProfileName('bad/slash'), isFalse);
    expect(profileNameFromFileName('Server-VPN.conf'), 'Server-VPN');
    expect(profileNameFromFileName('Plain'), 'Plain');
  });

  test('save/list/read/delete round-trips and de-dupes by name', () async {
    final store = SecureConfigStore(_MemKeyStore());

    await store.save('Server-VPN', '[Interface]\nA', deletable: false);
    await store.save('Home-Net', '[Interface]\nB', deletable: true);

    final names = (await store.list()).map((p) => p.name).toList();
    expect(names, ['Home-Net', 'Server-VPN']); // sorted, case-insensitive
    expect(await store.read('Server-VPN'), '[Interface]\nA');
    expect((await store.list()).firstWhere((p) => p.name == 'Home-Net').deletable, isTrue);

    // Re-saving the same name replaces, not duplicates.
    await store.save('Server-VPN', '[Interface]\nC');
    expect((await store.list()).where((p) => p.name == 'Server-VPN').length, 1);
    expect(await store.read('Server-VPN'), '[Interface]\nC');

    await store.delete('Server-VPN');
    expect((await store.list()).map((p) => p.name), ['Home-Net']);
    expect(await store.read('Server-VPN'), isNull);
  });

  test('rejects an invalid profile name', () async {
    final store = SecureConfigStore(_MemKeyStore());
    expect(
      () => store.save('bad name', '[Interface]'),
      throwsArgumentError,
    );
  });
}
