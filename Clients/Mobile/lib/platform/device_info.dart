import 'dart:io';

import 'package:device_info_plus/device_info_plus.dart';

/// Human-readable device name reported to the backend as `Hostname`, so a new
/// device is identifiable in the admin panel. Behind an interface so tests don't
/// hit platform channels.
abstract interface class DeviceInfoSource {
  Future<String> deviceName();
}

class PlatformDeviceInfo implements DeviceInfoSource {
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
