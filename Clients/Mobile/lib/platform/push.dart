import 'package:firebase_messaging/firebase_messaging.dart';

/// Source of the device's push (FCM) token, behind an interface so the rest of
/// the app doesn't depend on Firebase directly (and tests can fake it).
abstract interface class PushTokens {
  /// Requests notification permission and returns the FCM token, or null when
  /// push is unavailable (Firebase not configured / permission denied / error).
  Future<String?> token();
}

class FcmPushTokens implements PushTokens {
  @override
  Future<String?> token() async {
    try {
      final messaging = FirebaseMessaging.instance;
      await messaging.requestPermission();
      return await messaging.getToken();
    } catch (_) {
      // Firebase not configured (OSS build) or transient failure — no push.
      return null;
    }
  }
}
