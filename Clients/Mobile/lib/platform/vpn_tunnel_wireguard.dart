import 'package:valenius_vpn/valenius_vpn.dart';

import '../config/wg_normalize.dart';
import 'vpn_tunnel.dart';

/// Real tunnel backed by the `valenius_vpn` native plugin (Android: GoBackend).
/// Android runs **one** interface at a time, so [up] honours only the last peer
/// in the requested set; the controller already drives a single active profile.
class WireGuardVpnTunnel implements VpnTunnelPlatform {
  WireGuardVpnTunnel() : _plugin = ValeniusVpn();

  final ValeniusVpn _plugin;

  @override
  Future<bool> hasPermission() => _plugin.hasPermission();

  @override
  Future<bool> requestPermission() => _plugin.requestPermission();

  @override
  Future<void> up(List<TunnelConfig> peers) async {
    if (peers.isEmpty) {
      await _plugin.down();
      return;
    }
    final p = peers.last;
    if (!await _plugin.requestPermission()) {
      throw StateError('VPN permission denied');
    }
    // Android requires masked route prefixes — see normalizeAllowedIps.
    await _plugin.up(name: p.name, config: normalizeAllowedIps(p.wgQuickConf));
  }

  @override
  Future<void> down() => _plugin.down();

  @override
  Future<void> setOnDemand({required bool enabled, TunnelConfig? config}) =>
      _plugin.setOnDemand(
        enabled: enabled,
        name: config?.name,
        // Same route-masking the connect path applies (harmless on iOS).
        config: config == null ? null : normalizeAllowedIps(config.wgQuickConf),
      );

  @override
  Stream<TunnelSnapshot> states() => _plugin.states().map((e) {
        final st = switch (e.state) {
          'connected' => PeerState.connected,
          'connecting' => PeerState.connecting,
          _ => PeerState.down,
        };
        return TunnelSnapshot(
          peers: st == PeerState.down ? const {} : {e.name: st},
        );
      });

  @override
  Future<HandshakeInfo?> stats(String peerName) async {
    final s = await _plugin.stats(peerName);
    if (s == null) return null;
    return HandshakeInfo(
      lastHandshakeEpochSec: s.lastHandshakeEpochSec,
      rxBytes: s.rxBytes,
      txBytes: s.txBytes,
    );
  }
}
