import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../app/theme.dart';
import '../state/registration.dart';

/// Scans a pairing QR (generated in the admin panel) and redeems it to activate
/// this device. The QR encodes `{"v":1,"url":...,"token":...}`.
class PairScreen extends ConsumerStatefulWidget {
  const PairScreen({super.key});

  @override
  ConsumerState<PairScreen> createState() => _PairScreenState();
}

class _PairScreenState extends ConsumerState<PairScreen> {
  bool _handled = false;
  String? _status;

  static String? _extractToken(String raw) {
    try {
      final decoded = jsonDecode(raw);
      if (decoded is Map && decoded['v'] == 1 && decoded['token'] is String) {
        return decoded['token'] as String;
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
      final token = _extractToken(raw);
      if (token == null) {
        setState(() => _status = 'Not a Valenius pairing code');
        continue;
      }
      _handled = true;
      setState(() => _status = 'Pairing…');
      try {
        await ref.read(registrationControllerProvider.notifier).pair(token);
        if (mounted) Navigator.of(context).pop(true);
      } catch (e) {
        if (!mounted) return;
        _handled = false;
        setState(() => _status = 'Pairing failed: $e');
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
        title: const Text('Scan pairing code'),
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
