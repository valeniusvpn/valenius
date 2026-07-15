import 'dart:async';

import 'vpn_tunnel.dart';

/// No-op tunnel used until the native Android plugin lands (milestone 2).
/// Simulates connect/handshake so the UI can be built and exercised offline.
class StubVpnTunnel implements VpnTunnelPlatform {
  final _controller = StreamController<TunnelSnapshot>.broadcast();
  final Map<String, PeerState> _peers = {};

  @override
  Future<bool> hasPermission() async => true;

  @override
  Future<bool> requestPermission() async => true;

  @override
  Future<void> up(List<TunnelConfig> peers) async {
    final wanted = {for (final p in peers) p.name};
    _peers.removeWhere((name, _) => !wanted.contains(name));
    for (final p in peers) {
      _peers[p.name] = PeerState.connecting;
    }
    _emit();
    await Future<void>.delayed(const Duration(milliseconds: 500));
    for (final name in wanted) {
      _peers[name] = PeerState.connected;
    }
    _emit();
  }

  @override
  Future<void> down() async {
    _peers.clear();
    _emit();
  }

  @override
  Future<void> setOnDemand({required bool enabled, TunnelConfig? config}) async {}

  @override
  Stream<TunnelSnapshot> states() => _controller.stream;

  @override
  Future<HandshakeInfo?> stats(String peerName) async {
    if (_peers[peerName] != PeerState.connected) return null;
    return HandshakeInfo(
      lastHandshakeEpochSec: DateTime.now().millisecondsSinceEpoch ~/ 1000,
      rxBytes: 0,
      txBytes: 0,
    );
  }

  void _emit() => _controller.add(TunnelSnapshot(peers: Map.of(_peers)));
}
