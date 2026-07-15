import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../app/theme.dart';
import '../state/registration.dart';
import 'setup_qr_screen.dart';

/// Quick-config URL configured by tapping the logo three times. Injected at build time
/// (`--dart-define=VALENIUS_QUICK_CONFIG_URL=...`) so the managed-cloud host is never hardcoded
/// in the published source. Empty in dev/OSS builds → the triple-tap is a no-op there.
const _kQuickConfigUrl = String.fromEnvironment('VALENIUS_QUICK_CONFIG_URL');

/// First-run screen shown when the app has no backend URL. Offers the three ways to configure it:
///   1. Scan a QR code shown by the backend (also pairs the device if the QR carries a token).
///   2. Type the server address manually.
///   3. Triple-tap the logo → the build-time quick-config URL (managed cloud), if set.
class SetupScreen extends ConsumerStatefulWidget {
  const SetupScreen({super.key});

  @override
  ConsumerState<SetupScreen> createState() => _SetupScreenState();
}

class _SetupScreenState extends ConsumerState<SetupScreen> {
  int _logoTaps = 0;
  DateTime _lastTap = DateTime.fromMillisecondsSinceEpoch(0);

  void _onLogoTap() {
    final now = DateTime.now();
    // Reset the run if the taps are too far apart to be a deliberate triple-tap.
    _logoTaps = now.difference(_lastTap) > const Duration(milliseconds: 1500)
        ? 1
        : _logoTaps + 1;
    _lastTap = now;
    if (_logoTaps >= 3) {
      _logoTaps = 0;
      _applyQuickConfig();
    }
  }

  Future<void> _applyQuickConfig() async {
    // Only meaningful in a build that baked in the quick-config URL. Silent no-op otherwise.
    if (_kQuickConfigUrl.isEmpty) return;
    final url = await ref.read(backendUrlControllerProvider.notifier).set(_kQuickConfigUrl);
    if (url != null && mounted) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(
          content: Text('Server set to $url'),
          backgroundColor: ValeniusColors.verifiedPill,
        ));
    }
  }

  Future<void> _scanQr() async {
    await Navigator.of(context).push(
      MaterialPageRoute<bool>(builder: (_) => const SetupQrScreen()),
    );
    // The root gate rebuilds automatically once the URL is set; nothing to do here.
  }

  Future<void> _enterManually() async {
    final controller = TextEditingController();
    String? error;
    final dns = await showDialog<String>(
      context: context,
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setLocal) => AlertDialog(
          backgroundColor: ValeniusColors.hover,
          title: const Text('Server address', style: TextStyle(color: Colors.white)),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Enter your Valenius server name (your administrator provided it).',
                style: TextStyle(color: ValeniusColors.actionText, fontSize: 13),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: controller,
                autofocus: true,
                autocorrect: false,
                keyboardType: TextInputType.url,
                style: const TextStyle(color: Colors.white),
                decoration: InputDecoration(
                  // Fixed, non-editable scheme prefix — the user types only the host.
                  prefixText: 'https://',
                  prefixStyle: const TextStyle(color: ValeniusColors.dimText),
                  hintText: 'vpn.company.com',
                  hintStyle: const TextStyle(color: ValeniusColors.dimText),
                  errorText: error,
                  enabledBorder: const UnderlineInputBorder(
                    borderSide: BorderSide(color: ValeniusColors.sep),
                  ),
                ),
                onSubmitted: (v) => Navigator.of(ctx).pop(v),
              ),
            ],
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(ctx).pop(),
              child: const Text('Cancel'),
            ),
            TextButton(
              onPressed: () => Navigator.of(ctx).pop(controller.text),
              child: const Text('Save'),
            ),
          ],
        ),
      ),
    );
    if (dns == null) return;
    final url = await ref.read(backendUrlControllerProvider.notifier).set(dns);
    if (url == null && mounted) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(
          content: Text('Enter a valid server address, for example vpn.company.com'),
          backgroundColor: ValeniusColors.deleteHot,
        ));
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: ValeniusColors.bg,
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 28),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Logo doubles as the hidden triple-tap quick-config trigger.
              GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: _onLogoTap,
                child: Column(
                  children: [
                    Image.asset('assets/icon/valenius.png', height: 72),
                    const SizedBox(height: 12),
                    const Text(
                      'Valenius',
                      style: TextStyle(
                        color: Colors.white,
                        fontSize: 24,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 16),
              const Text(
                'Connect this app to your Valenius server to get started.',
                textAlign: TextAlign.center,
                style: TextStyle(color: ValeniusColors.actionText, fontSize: 14),
              ),
              const SizedBox(height: 32),
              FilledButton.icon(
                onPressed: _scanQr,
                icon: const Icon(Icons.qr_code_scanner),
                label: const Text('Scan QR code'),
                style: FilledButton.styleFrom(
                  backgroundColor: ValeniusColors.dotOn,
                  foregroundColor: ValeniusColors.bg,
                  padding: const EdgeInsets.symmetric(vertical: 14),
                ),
              ),
              const SizedBox(height: 12),
              OutlinedButton.icon(
                onPressed: _enterManually,
                icon: const Icon(Icons.keyboard),
                label: const Text('Enter server address'),
                style: OutlinedButton.styleFrom(
                  foregroundColor: ValeniusColors.actionText,
                  side: const BorderSide(color: ValeniusColors.sep),
                  padding: const EdgeInsets.symmetric(vertical: 14),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
