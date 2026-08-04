import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../config/lan_conflict.dart';
import '../config/wg_normalize.dart' show FallbackTarget, rewriteEndpointPort;
import '../diagnostics/diagnostics.dart';
import '../platform/health_probe.dart';
import '../platform/vpn_tunnel.dart';
import '../platform/vpn_tunnel_stub.dart';
import 'registration.dart' show registrationControllerProvider, remoteLanCidrsProvider;

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

// Explicit type annotation breaks the mutual-inference cycle with
// registrationControllerProvider (each reads the other's .notifier via ref.read).
final StateNotifierProvider<ConnectionController, VpnConnectionState>
    connectionControllerProvider =
    StateNotifierProvider<ConnectionController, VpnConnectionState>((ref) {
  return ConnectionController(
    ref.watch(vpnTunnelProvider),
    ref.watch(healthProbeProvider),
    networkChanges: ref.watch(connectivityChangesProvider),
    remoteLanCidrsByProfile: () => ref.read(remoteLanCidrsProvider),
    reportEvent: (eventType, tunnelName, {detail}) => ref
        .read(registrationControllerProvider.notifier)
        .logEvent(eventType, tunnelName, detail: detail),
  );
});

/// Which single profile is up, and whether it's been verified (Android runs one
/// interface at a time).
class VpnConnectionState {
  const VpnConnectionState({
    this.activeProfile,
    this.verified = false,
    this.verifiedViaGateway = false,
    this.fallbackPortUsed = 0,
    this.busy = false,
    this.error,
    this.errorAt,
    this.isLanConflict = false,
  });

  final String? activeProfile;
  final bool verified;

  /// Whether the last verification was an end-to-end gateway /health probe (vs a
  /// local handshake). A network-change re-verify trusts a failed gateway probe as
  /// "dead" only when the tunnel was confirmed this way.
  final bool verifiedViaGateway;

  /// The UDP port this connection switched to (typically 443) after the primary port
  /// produced no successful verification within the trigger window
  /// (docs/design/port-443-fallback.md), or 0 if it's still on the primary port. Sticky
  /// for the life of the connection — carried through `_markVerified`/re-verify state
  /// updates, cleared only by a fresh connect/disconnect. The UI appends " · <port>" to
  /// whichever tag it would otherwise show.
  final int fallbackPortUsed;
  final bool busy;

  /// Last connect/disconnect failure message, or null. [errorAt] lets the UI
  /// distinguish a fresh error from a repeat of the same message.
  final String? error;
  final DateTime? errorAt;

  /// True when [error] is a pre-connect local-LAN-conflict refusal (this device's own
  /// LAN overlaps a network reachable through the VPN, or the assigned VPN IP itself).
  /// The UI shows a blocking alert for this instead of the usual snackbar.
  final bool isLanConflict;

  bool isConnected(String name) => activeProfile == name;
}

class ConnectionController extends StateNotifier<VpnConnectionState> {
  ConnectionController(
    this._tunnel,
    this._probe, {
    Stream<void>? networkChanges,
    Map<String, List<String>> Function() remoteLanCidrsByProfile =
        _noRemoteLanCidrs,
    Future<void> Function(String eventType, String tunnelName, {String? detail})?
        reportEvent,
  })  : _remoteLanCidrsByProfile = remoteLanCidrsByProfile,
        _reportEvent = reportEvent ?? _noReportEvent,
        super(const VpnConnectionState()) {
    if (networkChanges != null) {
      _netSub = networkChanges.listen((_) => unawaited(_onNetworkChanged()));
    }
  }

  static Map<String, List<String>> _noRemoteLanCidrs() => const {};
  static Future<void> _noReportEvent(String eventType, String tunnelName,
      {String? detail}) async {}

  final VpnTunnelPlatform _tunnel;
  final HealthProbe _probe;

  /// Reports Connect/Disconnect/Verified/PortFallback to the backend (drives the admin
  /// client list's presence/connected indicators and connection history — see
  /// RegistrationController.logEvent). Defaults to a no-op so tests that don't care about
  /// backend reporting don't need to supply one.
  final Future<void> Function(String eventType, String tunnelName, {String? detail})
      _reportEvent;

  /// Per-profile physical LAN CIDR(s) behind that profile's own sidecar, from the latest
  /// heartbeat (`StatusResponse.RemoteLanCidrsByProfile[]`) — read lazily at connect time.
  /// Keyed by profile name; never apply one profile's entry to a different profile's
  /// connect (see the provider doc for why).
  final Map<String, List<String>> Function() _remoteLanCidrsByProfile;

  /// The gateway target for the active profile (if any), kept so a network-change
  /// re-verify can reuse the same /health probe the connect path used.
  GatewayTarget? _activeGateway;

  /// The fallback port/trigger for the active profile (if any), and the original
  /// decrypted config text — kept so `_verify` can rewrite the Endpoint port and bring
  /// the tunnel back up without re-reading the config store (docs/design/port-443-fallback.md).
  FallbackTarget? _activeFallback;
  String? _activeConfigText;

  /// Whether the fallback-port switch has already been attempted for the current
  /// connection — at most once per connect, mirroring the Windows `TunnelVerifier`.
  bool _fallbackTried = false;

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
  /// [fallback] (when the customer opted in and the sidecar confirmed it) lets
  /// verification retry once on the alternate UDP port if the primary appears blocked.
  Future<void> toggle(
    String name,
    String configText,
    GatewayTarget? gateway, {
    FallbackTarget? fallback,
  }) async {
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
      await _connect(name, configText, gateway, fallback);
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

  Future<void> _connect(
    String name,
    String configText,
    GatewayTarget? gateway,
    FallbackTarget? fallback,
  ) async {
    state = const VpnConnectionState(busy: true);
    try {
      // Pre-connect LAN-conflict check — mirrors the desktop clients. No override: the
      // conflict must be resolved (change local network, or ask an admin) before connecting.
      final localLanCidrs = await _tunnel.localLanCidrs();
      final conflict = findLocalLanConflict(
        configText: configText,
        localLanCidrs: localLanCidrs,
        remoteLanCidrs: _remoteLanCidrsByProfile()[name] ?? const [],
      );
      if (conflict != null) {
        DiagnosticsLog.instance.add('connect: "$name" refused (LAN conflict): $conflict');
        state = VpnConnectionState(
          error: conflict,
          errorAt: DateTime.now(),
          isLanConflict: true,
        );
        return;
      }

      await _tunnel.up([TunnelConfig(name: name, wgQuickConf: configText)]);
      _activeGateway = gateway;
      _activeFallback = fallback;
      _activeConfigText = configText;
      _fallbackTried = false;
      state = VpnConnectionState(activeProfile: name);
      DiagnosticsLog.instance.add('connect: "$name" up');
      unawaited(_reportEvent('Connect', name));
      unawaited(_verify(name, gateway));
    } catch (e) {
      _activeGateway = null;
      _activeFallback = null;
      _activeConfigText = null;
      DiagnosticsLog.instance.add('connect: "$name" failed: $e');
      state = VpnConnectionState(error: '$e', errorAt: DateTime.now());
    }
  }

  Future<void> _disconnect() async {
    final name = state.activeProfile;
    state = VpnConnectionState(activeProfile: state.activeProfile, busy: true);
    try {
      await _tunnel.down();
      _activeGateway = null;
      _activeFallback = null;
      _activeConfigText = null;
      state = const VpnConnectionState();
      if (name != null) unawaited(_reportEvent('Disconnect', name));
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

  /// The backend deleted this profile (foreign-profile deprovision, or a native
  /// deletion) and the config was just removed from the store — tear the tunnel
  /// down if it's the one currently active, so the app doesn't keep showing a
  /// "connected" profile whose config no longer exists. No-op otherwise.
  Future<void> onProfileDeleted(String name) async {
    final active = state.activeProfile;
    if (active == null || state.busy) return;
    if (active != name) return;
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
      final fallbackPortUsed = state.fallbackPortUsed;
      final gateway = _activeGateway;
      // Drop the green check while we re-confirm. fallbackPortUsed is carried through —
      // the tunnel itself isn't touched here (only re-probed), so whichever port it was
      // last brought up on is still the one it's on.
      state = VpnConnectionState(activeProfile: name, fallbackPortUsed: fallbackPortUsed);

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
          _activeFallback = null;
          _activeConfigText = null;
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
  ///
  /// Also owns the port-443 fallback retry (docs/design/port-443-fallback.md §5): if the
  /// customer's admin-configured trigger window (default 20 s, [FallbackTarget]) passes
  /// with no successful verification, it rewrites the config's Endpoint port and brings
  /// the tunnel back up once, then keeps polling — mirrors the Windows `TunnelVerifier`.
  /// The overall deadline is extended to at least `triggerSeconds + 15` when a fallback is
  /// configured, so the switch (usually ~10 s before the unmodified 30 s deadline) gets a
  /// fair window to verify instead of the loop giving up moments later.
  Future<void> _verify(String name, GatewayTarget? gateway) async {
    final start = DateTime.now();
    final fallback = _activeFallback;
    final minWindow = fallback != null
        ? Duration(seconds: fallback.triggerSeconds + 15)
        : const Duration(seconds: 30);
    final window = minWindow < const Duration(seconds: 30) ? const Duration(seconds: 30) : minWindow;
    final deadline = start.add(window);

    while (DateTime.now().isBefore(deadline)) {
      if (state.activeProfile != name) return; // disconnected / switched

      if (gateway != null && await _probe.ok(gateway)) {
        return _markVerified(name, viaGateway: true);
      }
      final s = await _tunnel.stats(name);
      if (s != null && _recentHandshake(s)) {
        return _markVerified(name, viaGateway: false);
      }

      if (fallback != null &&
          !_fallbackTried &&
          DateTime.now().difference(start).inSeconds >= fallback.triggerSeconds) {
        _fallbackTried = true;
        await _trySwitchToFallbackPort(name, fallback);
        if (state.activeProfile != name) return; // disconnected meanwhile
      }

      await Future<void>.delayed(const Duration(seconds: 2));
    }
  }

  /// Reinstalls the tunnel against [fallback]'s UDP port after no successful verification
  /// within its trigger window. Runs at most once per connect (guarded by the caller's
  /// `_fallbackTried` flag) — a single alternate-port attempt, not a permanent pin; the
  /// next connect always starts back on the primary port. On success, marks
  /// `state.fallbackPortUsed` (UI tag) and reports a best-effort `PortFallback` event to
  /// the backend (client connection history), same swallow-on-failure pattern as
  /// Connect/Disconnect/Verified.
  Future<void> _trySwitchToFallbackPort(String name, FallbackTarget fallback) async {
    final configText = _activeConfigText;
    if (configText == null) return;

    DiagnosticsLog.instance.add(
        'verify: "$name" not confirmed after ${fallback.triggerSeconds}s — retrying via UDP fallback port ${fallback.port}');
    try {
      await _tunnel.down();
      final rewritten = rewriteEndpointPort(configText, fallback.port);
      await _tunnel.up([TunnelConfig(name: name, wgQuickConf: rewritten)]);
      if (state.activeProfile != name) return; // disconnected while we were switching
      state = VpnConnectionState(activeProfile: name, fallbackPortUsed: fallback.port);
      unawaited(_reportEvent('PortFallback', name,
          detail:
              'Switched to UDP ${fallback.port} after ${fallback.triggerSeconds}s with no confirmation on the primary port.'));
    } catch (e) {
      DiagnosticsLog.instance.add('verify: "$name" fallback-port reconnect failed: $e');
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
        fallbackPortUsed: state.fallbackPortUsed,
      );
      // Mirrors Windows/Linux/macOS reporting a "Verified" event upstream — required so the
      // admin client list's "connected" star (which now demands actual gateway verification
      // for sidecar clients, not just Connect) ever lights up for this app. Deliberately only
      // the gateway-verified case reports; a handshake-only pass does not (handshake
      // freshness isn't a reliable enough signal to gate a display indicator on). Called from
      // both the initial post-connect verify and the network-change re-verify — the backend
      // side (WgTunnelTracker.MarkVerified) is idempotent, so reporting again on re-verify is
      // harmless and, unlike the desktop clients, keeps the star correct even if the initial
      // connect only managed a handshake pass before later gateway-verifying.
      if (viaGateway) unawaited(_reportEvent('Verified', name));
    }
  }
}
