import 'package:flutter_test/flutter_test.dart';
import 'package:valenius_mobile/platform/key_store.dart';

class _MapBackend implements SecretBackend {
  final Map<String, String> data = {};
  int writes = 0;
  bool failReadBack = false;

  @override
  Future<String?> read(String key) async =>
      failReadBack && key != legacyMigrationMarkerKey ? null : data[key];
  @override
  Future<void> write(String key, String value) async {
    writes++;
    data[key] = value;
  }

  @override
  Future<void> delete(String key) async => data.remove(key);
}

class _FakeLegacy implements LegacyStoreSource {
  _FakeLegacy(this.snapshot);
  LegacyStoreSnapshot snapshot;
  int reads = 0;
  int deletes = 0;
  bool deleteThrows = false;

  @override
  Future<LegacyStoreSnapshot> read() async {
    reads++;
    return snapshot;
  }

  @override
  Future<void> delete() async {
    deletes++;
    if (deleteThrows) throw Exception('io');
  }
}

void main() {
  final logs = <String>[];
  setUp(logs.clear);

  SecureKeyStore store(_MapBackend b, _FakeLegacy l) =>
      SecureKeyStore(backend: b, legacy: l, log: logs.add);

  test('copies every v9 entry before the first read, then deletes the old store',
      () async {
    final b = _MapBackend();
    final l = _FakeLegacy(const LegacyStoreSnapshot(status: 'ok', entries: {
      'valenius.clientKey': 'guid-1',
      'valenius.clientApiKey': 'vlnd_secret',
      'valenius.backendUrl': 'https://vpn.example',
    }));
    final s = store(b, l);

    expect(await s.read('valenius.clientKey'), 'guid-1');
    expect(await s.read('valenius.clientApiKey'), 'vlnd_secret');
    expect(b.data[legacyMigrationMarkerKey], 'migrated');
    expect(l.deletes, 1);
    expect(l.reads, 1, reason: 'handover runs once per store instance');
  });

  test('a later launch never reads the old store again', () async {
    final b = _MapBackend()..data[legacyMigrationMarkerKey] = 'migrated';
    final l = _FakeLegacy(const LegacyStoreSnapshot(
        status: 'ok', entries: {'valenius.clientKey': 'stale'}));
    b.data['valenius.clientKey'] = 'current';

    expect(await store(b, l).read('valenius.clientKey'), 'current');
    expect(l.reads, 0);
  });

  test('never overwrites a value already in the new store (crash mid-copy)',
      () async {
    final b = _MapBackend()..data['valenius.clientKey'] = 'already-copied';
    final l = _FakeLegacy(const LegacyStoreSnapshot(status: 'ok', entries: {
      'valenius.clientKey': 'already-copied',
      'valenius.backendUrl': 'https://vpn.example',
    }));

    await store(b, l).read('x');
    expect(b.data['valenius.backendUrl'], 'https://vpn.example');
    expect(b.data['valenius.clientKey'], 'already-copied');
  });

  test('failed read keeps the store unavailable for the session — no fresh identity',
      () async {
    final b = _MapBackend();
    final l = _FakeLegacy(const LegacyStoreSnapshot(
        status: 'failed', error: 'KeyStoreException: not ready'));
    final s = store(b, l);

    await expectLater(s.read('valenius.clientKey'),
        throwsA(isA<SecureStorageUnavailableException>()));
    await expectLater(s.write('valenius.clientKey', 'new-guid'),
        throwsA(isA<SecureStorageUnavailableException>()));
    expect(b.data, isEmpty, reason: 'nothing written, no marker');
    expect(l.reads, 1, reason: 'retried on the next launch, not per call');
    expect(l.deletes, 0);
  });

  test('next launch after a failure migrates normally', () async {
    final b = _MapBackend();
    await expectLater(
        store(b, _FakeLegacy(const LegacyStoreSnapshot(status: 'failed')))
            .read('k'),
        throwsA(isA<SecureStorageUnavailableException>()));

    final second = store(
        b,
        _FakeLegacy(const LegacyStoreSnapshot(
            status: 'ok', entries: {'valenius.clientKey': 'guid-1'})));
    expect(await second.read('valenius.clientKey'), 'guid-1');
  });

  test('abandoned (undecryptable for good) starts fresh and keeps the file',
      () async {
    final b = _MapBackend();
    final l = _FakeLegacy(
        const LegacyStoreSnapshot(status: 'abandoned', error: 'AEADBadTag'));

    expect(await store(b, l).read('valenius.clientKey'), isNull);
    expect(b.data[legacyMigrationMarkerKey], 'abandoned');
    expect(l.deletes, 0);
    expect(logs.single, contains('starting fresh'));
  });

  test('fresh install: nothing to migrate, marker written once', () async {
    final b = _MapBackend();
    final l = _FakeLegacy(const LegacyStoreSnapshot(status: 'none'));
    final s = store(b, l);

    expect(await s.read('valenius.clientKey'), isNull);
    await s.write('valenius.clientKey', 'guid');
    expect(b.data[legacyMigrationMarkerKey], 'none');
    expect(b.data['valenius.clientKey'], 'guid');
    expect(l.reads, 1);
  });

  test('read-back mismatch aborts before the marker and before deleting',
      () async {
    final b = _MapBackend()..failReadBack = true;
    final l = _FakeLegacy(const LegacyStoreSnapshot(
        status: 'ok', entries: {'valenius.clientKey': 'guid-1'}));

    await expectLater(store(b, l).read('valenius.clientKey'),
        throwsA(isA<SecureStorageUnavailableException>()));
    expect(b.data.containsKey(legacyMigrationMarkerKey), isFalse);
    expect(l.deletes, 0);
  });

  test('failing to delete the old file is harmless', () async {
    final b = _MapBackend();
    final l = _FakeLegacy(const LegacyStoreSnapshot(
        status: 'ok', entries: {'valenius.clientKey': 'guid-1'}))
      ..deleteThrows = true;

    expect(await store(b, l).read('valenius.clientKey'), 'guid-1');
    expect(b.data[legacyMigrationMarkerKey], 'migrated');
  });

  test('unknown native status is treated as a failure', () {
    final snap = LegacyStoreSnapshot.fromMap({'status': 'weird'});
    expect(
        migrateLegacyStore(_MapBackend(), _FakeLegacy(snap), logs.add),
        throwsA(isA<SecureStorageUnavailableException>()));
  });

  test('fromMap parses the platform channel payload', () {
    final snap = LegacyStoreSnapshot.fromMap({
      'status': 'ok',
      'entries': <Object?, Object?>{'a': '1'},
      'skipped': 2,
    });
    expect(snap.entries, {'a': '1'});
    expect(snap.skipped, 2);
  });
}
