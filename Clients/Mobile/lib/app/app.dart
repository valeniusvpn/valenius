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
    if (!urlState.configured) return const SetupScreen();
    return const HomeScreen();
  }
}
