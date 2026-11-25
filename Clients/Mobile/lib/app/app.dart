import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/registration.dart';
import '../ui/home_screen.dart';
import '../ui/setup_screen.dart';
import 'theme.dart';

class ValeniusApp extends StatelessWidget {
  const ValeniusApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Valenius',
      debugShowCheckedModeBanner: false,
      theme: buildValeniusTheme(),
      home: const _RootGate(),
    );
  }
}

/// Chooses the first screen based on whether a backend URL is configured. The app is published
/// with no backend URL; until one is set (setup_screen.dart) the home screen — which heartbeats
/// against that URL — must not mount.
class _RootGate extends ConsumerWidget {
  const _RootGate();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final urlState = ref.watch(backendUrlControllerProvider);
    if (!urlState.loaded) {
      // Still reading the persisted URL from secure storage — brief splash so a configured
      // device doesn't flash the setup screen on every launch.
      return const Scaffold(
        backgroundColor: ValeniusColors.bg,
        body: Center(child: CircularProgressIndicator(color: ValeniusColors.dotOn)),
      );
    }
    if (urlState.storageError != null) return const _StorageErrorScreen();
    if (!urlState.configured) return const SetupScreen();
    return const HomeScreen();
  }
}

/// Secure storage couldn't be read this launch (see SecureKeyStore). Deliberately a
/// dead end rather than the setup screen: setting the device up again would create a
/// second identity. Reopening the app retries.
class _StorageErrorScreen extends StatelessWidget {
  const _StorageErrorScreen();

  @override
  Widget build(BuildContext context) {
    return const Scaffold(
      backgroundColor: ValeniusColors.bg,
      body: Center(
        child: Padding(
          padding: EdgeInsets.all(32),
          child: Text(
            'Valenius could not open its secure storage.\n\n'
            'Please close the app completely and open it again. '
            'If this keeps happening, contact your administrator.',
            textAlign: TextAlign.center,
            style: TextStyle(color: ValeniusColors.profileText, fontSize: 16),
          ),
        ),
      ),
    );
  }
}
