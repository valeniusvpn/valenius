import 'package:flutter/material.dart';
import 'package:package_info_plus/package_info_plus.dart';

import '../app/theme.dart';

/// Shows an About dialog with the Valenius logo and the app version.
Future<void> showValeniusAbout(BuildContext context) async {
  final info = await PackageInfo.fromPlatform();
  if (!context.mounted) return;

  await showDialog<void>(
    context: context,
    builder: (dialogContext) => Dialog(
      backgroundColor: ValeniusColors.bg,
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            // The colour logo has dark text, so sit it on a white card.
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
              decoration: BoxDecoration(
                color: Colors.white,
                borderRadius: BorderRadius.circular(10),
              ),
              child: Image.asset('assets/logo/valenius_full.png', height: 44),
            ),
            const SizedBox(height: 20),
            Text(
              'Version ${info.version} (${info.buildNumber})',
              style: const TextStyle(color: ValeniusColors.profileText, fontSize: 14),
            ),
            const SizedBox(height: 6),
            const Text(
              'WireGuard® VPN client',
              style: TextStyle(color: ValeniusColors.dimText, fontSize: 12),
            ),
            const SizedBox(height: 20),
            Align(
              alignment: Alignment.centerRight,
              child: TextButton(
                onPressed: () => Navigator.of(dialogContext).pop(),
                style: TextButton.styleFrom(foregroundColor: ValeniusColors.dotOn),
                child: const Text('Close'),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}
