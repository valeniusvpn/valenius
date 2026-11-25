import 'dart:io';

/// Bumps the Android version code (the `+N` build number in pubspec.yaml) and then
/// runs `flutter build appbundle` (or `apk` with --apk).
///
/// Play Console rejects re-uploading a build whose version code was already
/// published, so every real release build needs a strictly higher number than the
/// last one actually uploaded. Building directly with `flutter build appbundle`
/// just reuses whatever number is already sitting in pubspec.yaml -- this script
/// exists so that number always advances first, whichever platform builds it.
///
/// Usage (from Clients/Mobile):
///   dart run tool/build_release.dart            # bump + build .aab (Play Store)
///   dart run tool/build_release.dart --apk       # bump + build .apk (sideloading)
///   dart run tool/build_release.dart --dry-run   # bump pubspec.yaml only, no build
///
/// Any other argument is forwarded verbatim to `flutter build`, e.g. the Play Store
/// build's quick-config host (never hardcoded in source -- see CLAUDE.md):
///   dart run tool/build_release.dart --dart-define=VALENIUS_QUICK_CONFIG_URL=https://<host>/
void main(List<String> args) {
  final apk = args.contains('--apk');
  final dryRun = args.contains('--dry-run');
  final passThrough =
      args.where((a) => a != '--apk' && a != '--dry-run').toList();

  final pubspecFile = File('pubspec.yaml');
  if (!pubspecFile.existsSync()) {
    stderr.writeln('pubspec.yaml not found -- run this from Clients/Mobile.');
    exitCode = 1;
    return;
  }

  final content = pubspecFile.readAsStringSync();
  final pattern = RegExp(r'^version:\s*(\d+\.\d+\.\d+)\+(\d+)\s*$', multiLine: true);
  final match = pattern.firstMatch(content);
  if (match == null) {
    stderr.writeln("Could not find a 'version: X.Y.Z+N' line in pubspec.yaml");
    exitCode = 1;
    return;
  }

  final versionName = match.group(1)!;
  final oldBuildNumber = int.parse(match.group(2)!);
  final newBuildNumber = oldBuildNumber + 1;

  if (dryRun) {
    stdout.writeln(
        'Dry run -- would bump version code $oldBuildNumber -> $newBuildNumber (version name $versionName unchanged). Nothing written, nothing built.');
    return;
  }

  final newContent = content.replaceRange(
    match.start,
    match.end,
    'version: $versionName+$newBuildNumber',
  );
  pubspecFile.writeAsStringSync(newContent);

  stdout.writeln(
      'Version code bumped: $oldBuildNumber -> $newBuildNumber (version name $versionName unchanged)');

  final target = apk ? 'apk' : 'appbundle';
  final flutterArgs = ['build', target, '--release', ...passThrough];
  stdout.writeln('Running: flutter ${flutterArgs.join(' ')}');
  final result = Process.runSync(
    'flutter',
    flutterArgs,
    runInShell: true,
  );
  stdout.write(result.stdout);
  stderr.write(result.stderr);
  exitCode = result.exitCode;
}
