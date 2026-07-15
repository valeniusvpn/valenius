import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../diagnostics/diagnostics.dart';
import '../platform/health_probe.dart';
import '../platform/vpn_tunnel.dart';
import '../platform/vpn_tunnel_stub.dart';

/// The tunnel implementation. `main.dart` overrides this with the native
/// WireGuard adapter in production; tests keep the stub.
final vpnTunnelProvider = Provider<VpnTunnelPlatform>((ref) => StubVpnTunnel());

final healthProbeProvider = Provider<HealthProbe>((ref) => HttpHealthProbe());

/// Fires whenever the OS reports a network change (Wi-Fi ↔ mobile, etc.). The
/// controller re-verifies the active tunnel on each event. Null disables the
/// listener — tests override it so no platform channel / stream is touched.
final connectivityChangesProvider = Provider<Stream<void>?>(
  (_) => Connectivity().onConnectivityChanged.map((_) {}),
);

final connectionControllerProvider =
    StateNotifierProvider<ConnectionController, VpnConnectionState>((ref) {
  return ConnectionController(
    ref.watch(vpnTunnelProvider),
    ref.watch(healthProbeProvider),
    networkChanges: ref.watch(connectivityChangesProvider),
  );
});

/// Which single profile is up, and whether it's been verified (Android runs one
/// interface at a time).
class VpnConnectionState {
  const VpnConnectionState({
    this.activeProfile,
    this.verified = false,
    this.verifiedViaGateway = false,
    this.busy = false,
    this.error,
    this.errorAt,
  });

  final String? activeProfile;
  final bool verified;

  /// Whether the last verification was an end-to-end gateway /health probe (vs a
  /// local handshake). A network-change re-verify trusts a failed gateway probe as
  /// "dead" only when the tunnel was confirmed this way.
  final bool verifiedViaGateway;
  final bool busy;

  /// Last connect/disconnect failure message, or null. [errorAt] lets the UI
  /// distinguish a fresh error from a repeat of the same message.
  final String? error;
  final DateTime? errorAt;

  bool isConnected(String name) => activeProfile == name;
}

class ConnectionController extends StateNotifier<VpnConnectionState> {
  ConnectionController(this._tunnel, this._probe, {Stream<void>? networkChanges})
      : super(const VpnConnectionState()) {
    if (networkChanges != null) {
      _netSub = networkChanges.listen((_) => unawaited(_onNetworkChanged()));
    }
  }

  final VpnTunnelPlatform _tunnel;
  final HealthProbe _probe;

  /// The gateway target for the active profile (if any), kept so a network-change
  /// re-verify can reuse the same /health probe the connect path used.
  GatewayTarget? _activeGateway;

  StreamSubscription<void>? _netSub;
  bool _reverifying = false;

  /// Set when the user manually disconnects, so the backend's on-demand
  /// auto-connect policy doesn't immediately re-assert the tunnel on the next
  /// heartbeat (which would make the disconnect button feel broken). Cleared when
  /// the user connects a profile again, or on app relaunch.
  bool _onDemandSuppressed = false;

  // After a network change, let the new path settle before probing, then
  // re-verify for up to the budget before declaring the tunnel dead.
  static const _reverifySettle = Duration(seconds: 5);
  static const _reverifyBudget = Duration(seconds: 15);

  @override
  void dispose() {
    _netSub?.cancel();
    super.dispose();
  }

  /// Tap a profile: connect it (replacing any active one), or disconnect if it's
  /// already the active profile. [gateway] (when the backend advertises one)
  /// enables the gateway /health probe; otherwise verification is handshake-only.
  Future<void> toggle(String name, String configText, GatewayTarget? gateway) async {
    if (state.activeProfile == name) {
      // A manual disconnect must stick: suppress the on-demand policy and clear
      // any OS on-demand rule so iOS doesn't reconnect immediately.
      _onDemandSuppressed = true;
      await _disconnect();
      try {
        await _tunnel.setOnDemand(enabled: false);
      } catch (_) {/* best-effort */}
    } else {
      _onDemandSuppressed = false;
      await _connect(name, configText, gateway);
    }
  }

  /// Apply the backend's on-demand auto-connect policy (iOS `NEOnDemandRule`;
  /// no-op on Android). Enables it only when the admin turned auto-connect on,
  /// the target profile's config is available, and the user hasn't just manually
  /// disconnected. The caller (heartbeat) already withholds [enabled] while an MFA
  /// gate is required, so on-demand never fights a gated peer.
  Future<void> applyOnDemand({
    required bool enabled,
    String? profileName,
    String? configText,
  }) async {
    final want = enabled &&
        !_onDemandSuppressed &&
        profileName != null &&
        configText != null;
    try {
      if (want) {
        await _tunnel.setOnDemand(
          enabled: true,
          config: TunnelConfig(name: profileName, wgQuickConf: configText),
        );
      } else {
        await _tunnel.setOnDemand(enabled: false);
      }
    } catch (e) {
      DiagnosticsLog.instance.add('on-demand apply failed: $e');
    }
  }

  Future<void> _connect(String name, String configText, GatewayTarget? gateway) async {
    state = const VpnConnectionState(busy: true);
    try {
      await _tunnel.up([TunnelConfig(name: name, wgQuickConf: configText)]);
      _activeGateway = gateway;
      state = VpnConnectionState(activeProfile: name);
      DiagnosticsLog.instance.add('connect: "$name" up');
      unawaited(_verify(name, gateway));
    } catch (e) {
      _activeGateway = null;
      DiagnosticsLog.instance.add('connect: "$name" failed: $e');
      state = VpnConnectionState(error: '$e', errorAt: DateTime.now());
    }
  }

  Future<void> _disconnect() async {
    state = VpnConnectionState(activeProfile: state.activeProfile, busy: true);
    try {
      await _tunnel.down();
      _activeGateway = null;
      state = const VpnConnectionState();
    } catch (e) {
      state = VpnConnectionState(
        activeProfile: state.activeProfile,
        error: '$e',
        errorAt: DateTime.now(),
      );
    }
  }

  /// The MFA gate returned (session expired): the sidecar has already dropped the
  /// peer, so the gated tunnel is dead. Tear it down so the UI matches the server and
  /// the user must re-authorize to reconnect. Only the gated server profile is torn
  /// down; a hand-connected foreign tunnel is left up. No-op if nothing is connected.
  Future<void> onMfaGate(String? gatedProfile) async {
    final active = state.activeProfile;
    if (active == null || state.busy) return;
    if (gatedProfile == null || gatedProfile != active) return;
    await _disconnect();
  }

  /// On a network change, re-confirm the active tunnel and tear it down only if we
  /// can be confident it's dead: it was verified end-to-end via the gateway /health
  /// probe and that probe now fails for the whole budget (the new network can't reach
  /// the sidecar). A tunnel that was only handshake-verified (no gateway, or a
  /// firewalled health port) is left connected — a stale handshake can't tell "dead"
  /// from "idle/roaming", so dropping it would risk killing a working VPN.
  Future<void> _onNetworkChanged() async {
    final name = state.activeProfile;
    if (name == null || state.busy || _reverifying) return;
    _reverifying = true;
    try {
      // Let the new path come up; our own connect/disconnect also moves the
      // network, so settle first and bail if anything changed meanwhile.
      await Future<void>.delayed(_reverifySettle);
      if (state.activeProfile != name || state.busy) return;

      final wasViaGateway = state.verifiedViaGateway;
      final gateway = _activeGateway;
      // Drop the green check while we re-confirm.
      state = VpnConnectionState(activeProfile: name);

      final deadline = DateTime.now().add(_reverifyBudget);

      if (gateway != null) {
        while (DateTime.now().isBefore(deadline)) {
          if (state.activeProfile != name) return; // disconnected / switched
          if (await _probe.ok(gateway)) return _markVerified(name, viaGateway: true);
          await Future<void>.delayed(const Duration(seconds: 2));
        }
        if (state.activeProfile != name) return;
        if (wasViaGateway) {
          // Trusted dead signal — tear it down.
          try {
            await _tunnel.down();
          } catch (_) {
            // best-effort; we still surface the lost connection below
          }
          _activeGateway = null;
          state = VpnConnectionState(
            error: 'VPN connection lost after a network change.',
            errorAt: DateTime.now(),
          );
          return;
        }
        // Gateway probe failed but was never gateway-verified (health firewalled) —
        // fall through to a handshake refresh and keep the tunnel.
      }

      // Non-gateway / firewalled-health: best-effort handshake refresh, no teardown.
      if (state.activeProfile != name) return;
      final s = await _tunnel.stats(name);
      if (s != null && _recentHandshake(s)) _markVerified(name, viaGateway: false);
    } finally {
      _reverifying = false;
    }
  }

  /// Background verification mirroring the Windows client: gateway /health probe
  /// through the tunnel (when advertised) with a handshake floor/fallback. Flips
  /// the row to "Verified". Retries ~30 s, then gives up (stays "Connected").
  Future<void> _verify(String name, GatewayTarget? gateway) async {
    final deadline = DateTime.now().add(const Duration(seconds: 30));
    while (DateTime.now().isBefore(deadline)) {
      if (state.activeProfile != name) return; // disconnected / switched

      if (gateway != null && await _probe.ok(gateway)) {
        return _markVerified(name, viaGateway: true);
      }
      final s = await _tunnel.stats(name);
      if (s != null && _recentHandshake(s)) {
        return _markVerified(name, viaGateway: false);
      }
      await Future<void>.delayed(const Duration(seconds: 2));
    }
  }

  bool _recentHandshake(HandshakeInfo s) {
    if (s.lastHandshakeEpochSec <= 0) return false;
    final nowSec = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    return nowSec - s.lastHandshakeEpochSec < 180;
  }

  void _markVerified(String name, {required bool viaGateway}) {
    if (state.activeProfile == name) {
      state = VpnConnectionState(
        activeProfile: name,
        verified: true,
        verifiedViaGateway: viaGateway,
      );
    }
  }
}
