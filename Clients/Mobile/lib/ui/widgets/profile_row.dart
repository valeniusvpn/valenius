import 'package:flutter/material.dart';

import '../../app/theme.dart';

/// One profile row, matching the Windows tray semantics: left indicator
/// (verified check-badge / connected dot / available dot), optional amber MFA
/// lock, and a right-side "Verified" pill or "Connected" tag. Tap toggles
/// connect ⇄ disconnect.
class ProfileRow extends StatelessWidget {
  const ProfileRow({
    super.key,
    required this.name,
    required this.connected,
    required this.verified,
    required this.mfaGated,
    required this.deletable,
    this.fallbackPort = 0,
    this.onTap,
  });

  final String name;
  final bool connected;
  final bool verified;
  final bool mfaGated;
  final bool deletable;

  /// The UDP port this connection switched to (typically 443) after the primary port
  /// produced no successful verification, or 0 if it's still on the primary port
  /// (docs/design/port-443-fallback.md). Appended to the "Verified"/"Connected" tag.
  final int fallbackPort;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      hoverColor: ValeniusColors.hover,
      splashColor: ValeniusColors.hover,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 16),
        child: Row(
          children: [
            _indicator(),
            const SizedBox(width: 14),
            Expanded(
              child: Text(
                name,
                style: const TextStyle(
                  color: ValeniusColors.profileText,
                  fontSize: 15,
                ),
              ),
            ),
            if (mfaGated && !connected)
              const Padding(
                padding: EdgeInsets.only(right: 8),
                child: Icon(Icons.lock, size: 16, color: ValeniusColors.lockAmber),
              ),
            _tag(),
          ],
        ),
      ),
    );
  }

  Widget _indicator() {
    if (verified) {
      return Container(
        width: 18,
        height: 18,
        decoration: const BoxDecoration(
          shape: BoxShape.circle,
          color: ValeniusColors.verifiedPill,
        ),
        child: const Icon(Icons.check, size: 12, color: Colors.white),
      );
    }
    return Container(
      width: 12,
      height: 12,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: connected ? ValeniusColors.dotOn : ValeniusColors.dotOff,
      ),
    );
  }

  Widget _tag() {
    final suffix = fallbackPort > 0 ? ' · $fallbackPort' : '';
    if (verified) {
      return Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: ValeniusColors.verifiedPill,
          borderRadius: BorderRadius.circular(10),
        ),
        child: Text(
          'Verified$suffix',
          style: const TextStyle(
            color: Colors.white,
            fontSize: 11,
            fontWeight: FontWeight.w600,
          ),
        ),
      );
    }
    if (connected) {
      return Text(
        'Connected$suffix',
        style: const TextStyle(color: ValeniusColors.connectedTag, fontSize: 12),
      );
    }
    return const SizedBox.shrink();
  }
}
