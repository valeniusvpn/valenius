/// Platform-neutral contract for the WireGuard tunnel.
///
/// This is **the** seam between shared Dart and native code. The Android
/// implementation lives in `packages/valenius_vpn/valenius_vpn_android`
/// (VpnService + GoBackend); iOS plugs in later (NEPacketTunnelProvider +
/// WireGuardKit). Nothing above this interface may touch a platform API.
///
/// Android exposes a single system VPN, so [up] takes the full set of desired
/// peers (merged into one VpnService) rather than connecting tunnels one by one.
abstract interface class VpnTunnelPlatform {
  Future<bool> hasPermission();
  Future<bool> requestPermission();

  /// Bring up one VpnService carrying [peers] (already merge-checked, non-conflicting).
  Future<void> up(List<TunnelConfig> peers);
  Future<void> down();

  Stream<TunnelSnapshot> states();
  Future<HandshakeInfo?> stats(String peerName);

  /// Configure OS-managed on-demand auto-connect (iOS `NEOnDemandRule`; a no-op
  /// where the OS has no equivalent, e.g. Android). When [enabled], the OS brings
  /// the tunnel up on network activity — even with the app not running — and
  /// re-asserts it after a manual disconnect, so a caller that wants a manual
  /// disconnect to stick must disable on-demand first. [config] must be supplied
  /// when enabling (there has to be a tunnel to bring up); ignored when disabling.
  Future<void> setOnDemand({required bool enabled, TunnelConfig? config});
}

enum PeerState { down, connecting, connected }

class TunnelConfig {
  const TunnelConfig({required this.name, required this.wgQuickConf});

  final String name;

  /// Decrypted wg-quick config text. Held in-memory only — never persisted plain.
  final String wgQuickConf;
}

class TunnelSnapshot {
  const TunnelSnapshot({required this.peers});
  final Map<String, PeerState> peers;
}

class HandshakeInfo {
  const HandshakeInfo({
    required this.lastHandshakeEpochSec,
    required this.rxBytes,
    required this.txBytes,
  });

  final int lastHandshakeEpochSec;
  final int rxBytes;
  final int txBytes;
}
