import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:local_auth/local_auth.dart';

import '../api/models.dart';
import '../app/theme.dart';
import '../state/registration.dart';

/// Shown on an approver phone when a cross-device approval is pending. "Review"
/// opens a number-match sheet; picking the number that matches the requesting
/// device, then a biometric, approves it.
class ApproveBanner extends ConsumerWidget {
  const ApproveBanner({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pending = ref.watch(pendingApprovalsProvider);
    if (pending.isEmpty) return const SizedBox.shrink();
    final approval = pending.first;
    return Container(
      color: ValeniusColors.lockAmber.withValues(alpha: 0.16),
      padding: const EdgeInsets.fromLTRB(16, 10, 8, 10),
      child: Row(
        children: [
          const Icon(Icons.verified_user, size: 18, color: ValeniusColors.lockAmber),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              'Approve VPN for ${approval.requesterName}?',
              style: const TextStyle(color: ValeniusColors.profileText, fontSize: 13),
            ),
          ),
          TextButton(
            onPressed: () => _review(context, ref, approval),
            style: TextButton.styleFrom(foregroundColor: ValeniusColors.lockAmber),
            child: const Text('Review'),
          ),
        ],
      ),
    );
  }

  Future<void> _review(BuildContext context, WidgetRef ref, PendingApproval a) async {
    final result = await showModalBottomSheet<({int? number, bool deny})>(
      context: context,
      backgroundColor: ValeniusColors.bg,
      builder: (_) => _ApproveSheet(approval: a),
    );
    if (result == null) return;

    final controller = ref.read(registrationControllerProvider.notifier);
    if (result.deny) {
      await controller.denyChallenge(a.challengeId);
      return;
    }
    final number = result.number;
    if (number == null) return;

    if (!await _biometricOk()) return; // user cancelled / failed biometric
    final approved = await controller.approveChallenge(a.challengeId, number);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text(approved ? 'Approved' : 'Approval failed (wrong number or expired)'),
      backgroundColor: approved ? ValeniusColors.verifiedPill : ValeniusColors.deleteHot,
    ));
  }

  Future<bool> _biometricOk() async {
    try {
      return await LocalAuthentication().authenticate(
        localizedReason: 'Approve the VPN connection',
        options: const AuthenticationOptions(stickyAuth: true),
      );
    } catch (_) {
      return false;
    }
  }
}

class _ApproveSheet extends StatelessWidget {
  const _ApproveSheet({required this.approval});

  final PendingApproval approval;

  @override
  Widget build(BuildContext context) {
    final subtitle = approval.requesterIp == null
        ? approval.requesterName
        : '${approval.requesterName} · ${approval.requesterIp}';
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text(
              'Approve VPN connection',
              style: TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w600),
            ),
            const SizedBox(height: 4),
            Text(subtitle,
                style: const TextStyle(color: ValeniusColors.actionText, fontSize: 13)),
            const SizedBox(height: 18),
            const Text(
              'Tap the number shown on the requesting device',
              textAlign: TextAlign.center,
              style: TextStyle(color: ValeniusColors.dimText, fontSize: 12),
            ),
            const SizedBox(height: 12),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceEvenly,
              children: [
                for (final n in approval.choices)
                  ElevatedButton(
                    onPressed: () =>
                        Navigator.of(context).pop((number: n, deny: false)),
                    style: ElevatedButton.styleFrom(
                      backgroundColor: ValeniusColors.hover,
                      foregroundColor: Colors.white,
                      minimumSize: const Size(72, 56),
                    ),
                    child: Text('$n',
                        style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600)),
                  ),
              ],
            ),
            const SizedBox(height: 16),
            TextButton(
              onPressed: () => Navigator.of(context).pop((number: null, deny: true)),
              style: TextButton.styleFrom(foregroundColor: ValeniusColors.deleteHot),
              child: const Text('Deny'),
            ),
          ],
        ),
      ),
    );
  }
}
