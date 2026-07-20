import 'package:flutter/material.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import '../app/theme.dart';

const _privacyPolicyUrl = 'https://www.valenius.com/privacy-mobile.html';

/// Shows an About dialog with the Valenius logo, app version, developer
/// attribution, and a link to the mobile privacy policy.
Future<void> showValeniusAbout(BuildContext context) async {
  // Read fresh from the platform every time rather than caching, so the
  // dialog always reflects the version of the APK actually installed.
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
              'Version ${info.version}',
              style: const TextStyle(color: ValeniusColors.profileText, fontSize: 14),
            ),
            const SizedBox(height: 6),
            const Text(
              'WireGuard® VPN client',
              style: TextStyle(color: ValeniusColors.dimText, fontSize: 12),
            ),
            const SizedBox(height: 20),
            const Text(
              'Developed by Stranto Business Solutions GmbH',
              style: TextStyle(color: ValeniusColors.dimText, fontSize: 12),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 4),
            InkWell(
              onTap: () => launchUrl(
                Uri.parse(_privacyPolicyUrl),
                mode: LaunchMode.externalApplication,
              ),
              child: const Text(
                'Privacy Policy',
                style: TextStyle(
                  color: ValeniusColors.actionText,
                  fontSize: 12,
                  decoration: TextDecoration.underline,
                ),
              ),
            ),
            const SizedBox(height: 12),
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
