import 'package:flutter/services.dart';

/// Dart API over the native single-tunnel WireGuard backend (Android: GoBackend).
/// One tunnel is active at a time; [up] replaces whatever is currently running.
class ValeniusVpn {
  static const MethodChannel _method = MethodChannel('valenius_vpn');
  static const EventChannel _events = EventChannel('valenius_vpn/states');

  /// True if the OS VPN consent has already been granted.
  Future<bool> hasPermission() async =>
      await _method.invokeMethod<bool>('hasPermission') ?? false;

  /// Shows the system VPN-consent dialog if needed; true once granted.
  Future<bool> requestPermission() async =>
      await _method.invokeMethod<bool>('requestPermission') ?? false;

  /// Bring up [config] (wg-quick text) under [name], replacing any active tunnel.
  Future<void> up({required String name, required String config}) =>
      _method.invokeMethod<void>('up', {'name': name, 'config': config});

  Future<void> down() => _method.invokeMethod<void>('down');

  /// Enable/disable OS-managed on-demand auto-connect (iOS `NEOnDemandRule`).
  /// When enabling, [name]/[config] give the tunnel the OS should bring up on
  /// network activity. No-op on platforms without an equivalent (Android).
  Future<void> setOnDemand({
    required bool enabled,
    String? name,
    String? config,
  }) =>
      _method.invokeMethod<void>('setOnDemand', {
        'enabled': enabled,
        'name': name,
        'config': config,
      });

  Future<TunnelStats?> stats(String name) async {
    final m = await _method
        .invokeMapMethod<String, dynamic>('stats', {'name': name});
    if (m == null) return null;
    return TunnelStats(
      lastHandshakeEpochSec: (m['lastHandshakeEpochSec'] as num).toInt(),
      rxBytes: (m['rxBytes'] as num).toInt(),
      txBytes: (m['txBytes'] as num).toInt(),
    );
  }

  /// This device's own local LAN CIDR(s) (e.g. "192.168.1.0/24"), one per active
  /// non-loopback, non-VPN Wi-Fi/ethernet/cellular link. Used by the pre-connect
  /// LAN-conflict check. Empty on any failure (fail-open, matches the desktop clients).
  Future<List<String>> localLanCidrs() async {
    final result = await _method.invokeListMethod<String>('localLanCidrs');
    return result ?? const [];
  }

  /// Stream of `{name, state}` where state is connecting | connected | down.
  Stream<TunnelStateEvent> states() =>
      _events.receiveBroadcastStream().map((dynamic e) {
        final m = (e as Map).cast<String, dynamic>();
        return TunnelStateEvent(
          name: m['name'] as String,
          state: m['state'] as String,
        );
      });
}

class TunnelStats {
  const TunnelStats({
    required this.lastHandshakeEpochSec,
    required this.rxBytes,
    required this.txBytes,
  });

  final int lastHandshakeEpochSec;
  final int rxBytes;
  final int txBytes;
}

class TunnelStateEvent {
  const TunnelStateEvent({required this.name, required this.state});
  final String name;
  final String state;
}
