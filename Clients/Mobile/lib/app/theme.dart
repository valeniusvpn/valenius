import 'package:flutter/material.dart';

/// Palette lifted verbatim from the Windows tray (`TrayPopupForm.cs`) so the
/// mobile client reads as the same product. Do **not** invent new tokens —
/// extend this list and reference it everywhere.
abstract final class ValeniusColors {
  static const bg = Color(0xFF162030); // CBg
  static const hover = Color(0xFF26364E); // CHover
  static const sep = Color(0xFF30425C); // CSep
  static const profileText = Color(0xFFDCE8F8); // CProfileText
  static const actionText = Color(0xFFC3D4EB); // CActionText
  static const dotOn = Color(0xFF00D26E); // CDotOn
  static const dotOff = Color(0xFF5A6C82); // CDotOff
  static const connectedTag = Color(0xFF00DC73); // CConnectedTag
  static const verifiedPill = Color(0xFF00C86C); // CVerifiedPill
  static const dimText = Color(0xFF50647D); // CDimText
  static const lockAmber = Color(0xFFD2A037); // CLockAmber
  static const deleteHot = Color(0xFFDC5050); // delete x (hover)
}

ThemeData buildValeniusTheme() {
  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    scaffoldBackgroundColor: ValeniusColors.bg,
    colorScheme: ColorScheme.fromSeed(
      seedColor: ValeniusColors.dotOn,
      brightness: Brightness.dark,
    ).copyWith(surface: ValeniusColors.bg),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(foregroundColor: ValeniusColors.actionText),
    ),
  );
}
