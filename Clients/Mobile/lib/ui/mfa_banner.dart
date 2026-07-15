import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../app/theme.dart';
import '../state/registration.dart';

/// Amber banner shown when TOTP MFA needs attention: enrollment setup, or
/// authorization to lift the gate so the peer can connect. Mirrors the Windows/
/// Linux clients (opens the same authorize/enrollment links).
class MfaBanner extends ConsumerWidget {
  const MfaBanner({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mfa = ref.watch(mfaStateProvider);

    if (mfa.enrollmentOpen && mfa.enrollmentUri != null) {
      return _banner(
        text: 'Set up two-factor authentication',
        actionLabel: 'Set up',
        onAction: () => _enroll(context, ref, mfa.enrollmentUri!),
      );
    }
    if (mfa.required && mfa.authorizeUrl != null) {
      return _banner(
        text: 'Authorization required to connect',
        actionLabel: 'Authorize',
        onAction: () => _open(mfa.authorizeUrl!),
      );
    }
    return const SizedBox.shrink();
  }

  Widget _banner({
    required String text,
    required String actionLabel,
    required VoidCallback onAction,
  }) {
    return Container(
      color: ValeniusColors.lockAmber.withValues(alpha: 0.16),
      padding: const EdgeInsets.fromLTRB(16, 10, 8, 10),
      child: Row(
        children: [
          const Icon(Icons.lock, size: 18, color: ValeniusColors.lockAmber),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              style: const TextStyle(color: ValeniusColors.profileText, fontSize: 13),
            ),
          ),
          TextButton(
            onPressed: onAction,
            style: TextButton.styleFrom(foregroundColor: ValeniusColors.lockAmber),
            child: Text(actionLabel),
          ),
        ],
      ),
    );
  }

  Future<void> _open(String url) async {
    await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
  }

  Future<void> _enroll(BuildContext context, WidgetRef ref, String uri) async {
    // otpauth:// opens the user's authenticator app to add the account.
    await launchUrl(Uri.parse(uri), mode: LaunchMode.externalApplication);
    if (!context.mounted) return;

    final code = await showDialog<String>(
      context: context,
      builder: (_) => const _EnrollCodeDialog(),
    );
    if (code == null || code.isEmpty) return;

    final ok =
        await ref.read(registrationControllerProvider.notifier).confirmEnrollment(code);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text(ok ? 'Two-factor authentication enabled' : 'Invalid code'),
      backgroundColor: ok ? ValeniusColors.verifiedPill : ValeniusColors.deleteHot,
    ));
  }
}

class _EnrollCodeDialog extends StatefulWidget {
  const _EnrollCodeDialog();

  @override
  State<_EnrollCodeDialog> createState() => _EnrollCodeDialogState();
}

class _EnrollCodeDialogState extends State<_EnrollCodeDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Confirm setup'),
      content: TextField(
        controller: _controller,
        keyboardType: TextInputType.number,
        autofocus: true,
        maxLength: 6,
        decoration: const InputDecoration(
          labelText: 'Authenticator code',
          counterText: '',
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        TextButton(
          onPressed: () => Navigator.of(context).pop(_controller.text.trim()),
          child: const Text('Confirm'),
        ),
      ],
    );
  }
}
