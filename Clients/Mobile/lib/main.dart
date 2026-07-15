import 'package:firebase_core/firebase_core.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app/app.dart';
import 'platform/vpn_tunnel_wireguard.dart';
import 'state/app_state.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // FCM (push-to-approve) is Pro/optional. Initialise Firebase only if it's
  // configured (google-services.json present); otherwise carry on without push.
  try {
    await Firebase.initializeApp();
  } catch (e) {
    debugPrint('Firebase not configured, push disabled: $e');
  }

  runApp(
    ProviderScope(
      // Production uses the native WireGuard tunnel; tests fall back to the
      // stub default in `vpnTunnelProvider`.
      overrides: [
        vpnTunnelProvider.overrideWithValue(WireGuardVpnTunnel()),
      ],
      child: const ValeniusApp(),
    ),
  );
}
