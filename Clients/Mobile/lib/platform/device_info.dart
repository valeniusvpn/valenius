import 'dart:io';

import 'package:android_id/android_id.dart';
import 'package:device_info_plus/device_info_plus.dart';

/// Human-readable device name reported to the backend as `Hostname`, so a new
/// device is identifiable in the admin panel. Behind an interface so tests don't
/// hit platform channels.
abstract interface class DeviceInfoSource {
  Future<String> deviceName();
  Future<String?> deviceId();
}

class PlatformDeviceInfo implements DeviceInfoSource {
  @override
  Future<String?> deviceId() async {
    if (!Platform.isAndroid) return null;

    try {
      final id = (await const AndroidId().getId())?.trim();
      return id == null || id.isEmpty ? null : id;
    } catch (_) {
      // Registration remains usable on devices where the platform API/plugin cannot
      // provide an ID. The backend explicitly skips Android duplicate detection then.
      return null;
    }
  }

  @override
  Future<String> deviceName() async {
    final info = DeviceInfoPlugin();
    if (Platform.isAndroid) {
      final a = await info.androidInfo;
      final name = '${a.manufacturer} ${a.model}'.trim();
      return name.isEmpty ? 'Android device' : name;
    }
    if (Platform.isIOS) {
      final i = await info.iosInfo;
      return i.name; // user-assigned device name
    }
    return 'Mobile device';
  }
}
