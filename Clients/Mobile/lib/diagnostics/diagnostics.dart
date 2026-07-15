import 'dart:collection';
import 'dart:convert';
import 'dart:io';

/// A small in-memory rolling log of app events. Android has no accessible system
/// log for an ordinary app, so this is the client's own event trail — uploaded
/// (gzipped + redacted) to the backend for support/error analysis. Bounded so it
/// can't grow without limit; oldest lines drop off.
class DiagnosticsLog {
  DiagnosticsLog._();
  static final DiagnosticsLog instance = DiagnosticsLog._();

  static const _maxLines = 500;
  final Queue<String> _lines = Queue<String>();

  /// Records one timestamped line. Callers should avoid logging secrets, but a
  /// redaction pass in [buildGzippedBundle] is the backstop.
  void add(String message) {
    _lines.addLast('[${DateTime.now().toUtc().toIso8601String()}] $message');
    while (_lines.length > _maxLines) {
      _lines.removeFirst();
    }
  }

  String dump() => _lines.join('\n');

  /// Builds the gzipped, redacted diagnostic bundle for upload.
  List<int> buildGzippedBundle() {
    final text = '===== Summary =====\n'
        'Collected: ${DateTime.now().toUtc().toIso8601String()}\n'
        'Platform:  ${Platform.operatingSystem} ${Platform.operatingSystemVersion}\n'
        '\n===== App log =====\n${dump()}\n';
    return gzip.encode(utf8.encode(_redact(text)));
  }

  /// Strips secrets so nothing sensitive leaves the device.
  static String _redact(String text) {
    text = text.replaceAllMapped(
        RegExp(r'^(\s*(?:PrivateKey|PresharedKey))\s*=\s*.+$', multiLine: true, caseSensitive: false),
        (m) => '${m[1]} = [REDACTED]');
    text = text.replaceAll(RegExp(r'[A-Za-z0-9+/]{43}='), '[REDACTED-KEY]');
    text = text.replaceAllMapped(
        RegExp(r'(secret|token|authorization|bearer|apikey|x-api-key)\s*[:=]\s*\S+', caseSensitive: false),
        (m) => '${m[1]}=[REDACTED]');
    return text;
  }
}
