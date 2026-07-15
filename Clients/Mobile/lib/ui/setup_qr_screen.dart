import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../api/identity.dart';
import '../app/theme.dart';
import '../state/registration.dart';

/// First-run QR scanner that configures the backend URL. The backend's pairing QR encodes
/// `{"v":1,"url":...,"token":...}`; a plain `{"v":1,"url":...}` (or a bare https URL) configures the
/// URL only. When a token is present the device is also paired (activated) in the same step, using a
/// one-off client — the registration controller doesn't exist yet at this point (no URL until now).
class SetupQrScreen extends ConsumerStatefulWidget {
  const SetupQrScreen({super.key});

  @override
  ConsumerState<SetupQrScreen> createState() => _SetupQrScreenState();
}

class _SetupQrScreenState extends ConsumerState<SetupQrScreen> {
  bool _handled = false;
  String? _status;

  /// Returns (url, token) from a scanned code, or null if it isn't one of ours.
  static ({String? url, String? token})? _parse(String raw) {
    final t = raw.trim();
    if (t.startsWith('http://') || t.startsWith('https://')) {
      return (url: t, token: null);
    }
    try {
      final decoded = jsonDecode(t);
      if (decoded is Map && decoded['v'] == 1) {
        final url = decoded['url'] is String ? decoded['url'] as String : null;
        final token = decoded['token'] is String ? decoded['token'] as String : null;
        if (url != null || token != null) return (url: url, token: token);
      }
    } catch (_) {
      // not JSON / not ours
    }
    return null;
  }

  Future<void> _onDetect(BarcodeCapture capture) async {
    if (_handled) return;
    for (final barcode in capture.barcodes) {
      final raw = barcode.rawValue;
      if (raw == null) continue;
      final parsed = _parse(raw);
      if (parsed == null) {
        setState(() => _status = 'Not a Valenius code');
        continue;
      }
      if (parsed.url == null) {
        setState(() => _status = 'This code has no server address');
        continue;
      }
      _handled = true;
      setState(() => _status = 'Configuring…');
      try {
        final url = await ref.read(backendUrlControllerProvider.notifier).set(parsed.url!);
        if (url == null) {
          _handled = false;
          setState(() => _status = 'Invalid server address in code');
          return;
        }
        // If the QR also carries a pairing token, activate the device now against the just-set
        // URL. Best-effort: if it fails, the URL is still saved and the user can pair from the
        // home screen. The registration controller isn't up yet, so use a one-off client.
        if (parsed.token != null) {
          setState(() => _status = 'Pairing…');
          try {
            final keyStore = ref.read(keyStoreProvider);
            final identity = await ClientIdentity.ensure(keyStore);
            final hostname = await ref.read(deviceInfoProvider).deviceName();
            final api = ref.read(backendFactoryProvider)(url, identity.clientKey);
            // Pairing returns this installation's API key — persist it so the registration
            // controller (which starts once the home screen mounts) authenticates with it.
            final apiKey = await api.redeemPairing(parsed.token!, hostname: hostname);
            if (apiKey != null && apiKey.isNotEmpty) {
              await keyStore.write(kClientApiKeyStorageKey, apiKey);
            }
            api.close();
          } catch (_) {
            // URL is set; pairing can be retried from the home screen.
          }
        }
        if (mounted) Navigator.of(context).pop(true);
      } catch (e) {
        if (!mounted) return;
        _handled = false;
        setState(() => _status = 'Setup failed: $e');
      }
      return;
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: ValeniusColors.bg,
      appBar: AppBar(
        backgroundColor: ValeniusColors.bg,
        foregroundColor: Colors.white,
        title: const Text('Scan server QR code'),
      ),
      body: Stack(
        alignment: Alignment.bottomCenter,
        children: [
          MobileScanner(onDetect: _onDetect),
          if (_status != null)
            Padding(
              padding: const EdgeInsets.only(bottom: 48),
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                decoration: BoxDecoration(
                  color: Colors.black87,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  _status!,
                  style: const TextStyle(color: Colors.white, fontSize: 14),
                ),
              ),
            ),
        ],
      ),
    );
  }
}
