import Darwin
import Flutter
import NetworkExtension

/// App-process side of the Valenius WireGuard tunnel. Mirrors the Android
/// plugin's channel contract exactly (`valenius_vpn` MethodChannel +
/// `valenius_vpn/states` EventChannel), so the shared Dart adapter
/// (`lib/platform/vpn_tunnel_wireguard.dart`) is reused unchanged.
///
/// The key iOS difference vs. Android: the WireGuard tunnel runs in a *separate*
/// Network Extension process (see `PacketTunnelProvider.swift`). This class only
/// *manages* that extension via `NETunnelProviderManager` and talks to it over
/// provider messages — it never touches the tunnel data path itself.
///
/// Single active tunnel at a time (like Android): `up` reuses the one saved
/// manager and replaces its configuration.
public class ValeniusVpnPlugin: NSObject, FlutterPlugin, FlutterStreamHandler {

  /// The Network Extension target's bundle id — the host app id + ".tunnel".
  /// Derived at runtime so a bundle-id change doesn't silently break tunnel start.
  private var tunnelBundleId: String {
    (Bundle.main.bundleIdentifier ?? "com.stranto.valenius.valeniusMobile") + ".tunnel"
  }

  private var eventSink: FlutterEventSink?
  private var manager: NETunnelProviderManager?
  private var statusObserver: NSObjectProtocol?
  private var currentName: String?

  public static func register(with registrar: FlutterPluginRegistrar) {
    let instance = ValeniusVpnPlugin()
    let method = FlutterMethodChannel(name: "valenius_vpn",
                                      binaryMessenger: registrar.messenger())
    registrar.addMethodCallDelegate(instance, channel: method)
    let events = FlutterEventChannel(name: "valenius_vpn/states",
                                     binaryMessenger: registrar.messenger())
    events.setStreamHandler(instance)
  }

  // MARK: - MethodChannel

  public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
    switch call.method {
    case "hasPermission":
      hasPermission(result)
    case "requestPermission":
      // On iOS, VPN consent is inseparable from saving a configuration, which
      // happens in `up` (the OS shows "Allow VPN configuration?" on first save).
      // There's nothing to prompt without a config, so report ready and let `up`
      // surface a denial as a start error — matches how NetworkExtension works.
      result(true)
    case "up":
      guard let a = call.arguments as? [String: Any],
            let name = a["name"] as? String,
            let config = a["config"] as? String else {
        result(FlutterError(code: "bad_args", message: "name/config required", details: nil))
        return
      }
      up(name: name, config: config, result: result)
    case "down":
      down(result)
    case "stats":
      let name = (call.arguments as? [String: Any])?["name"] as? String
      stats(name: name, result: result)
    case "localLanCidrs":
      result(Self.localLanCidrs())
    case "setOnDemand":
      let a = call.arguments as? [String: Any]
      setOnDemand(enabled: (a?["enabled"] as? Bool) ?? false,
                  name: a?["name"] as? String,
                  config: a?["config"] as? String,
                  result: result)
    default:
      result(FlutterMethodNotImplemented)
    }
  }

  private func hasPermission(_ result: @escaping FlutterResult) {
    NETunnelProviderManager.loadAllFromPreferences { managers, error in
      // A saved manager implies the one-time system VPN consent was granted.
      result(error == nil && (managers?.isEmpty == false))
    }
  }

  private func up(name: String, config: String, result: @escaping FlutterResult) {
    NETunnelProviderManager.loadAllFromPreferences { [weak self] managers, error in
      guard let self = self else { return }
      if let error = error {
        result(FlutterError(code: "load_failed", message: error.localizedDescription, details: nil))
        return
      }

      // Reuse the single saved manager if present, else create one.
      let mgr = managers?.first ?? NETunnelProviderManager()

      let proto = NETunnelProviderProtocol()
      proto.providerBundleIdentifier = self.tunnelBundleId
      // serverAddress is display-only for iOS; use the peer endpoint host if we
      // can find it, else a stable label.
      proto.serverAddress = Self.endpointHost(from: config) ?? "Valenius"
      // The extension reads the wg-quick text from here at startTunnel time, so
      // the Dart config store stays the single source of truth (no Keychain in
      // the extension).
      proto.providerConfiguration = ["wgQuickConfig": config, "name": name]

      mgr.protocolConfiguration = proto
      mgr.localizedDescription = name
      mgr.isEnabled = true

      mgr.saveToPreferences { saveErr in
        if let saveErr = saveErr {
          result(FlutterError(code: "save_failed", message: saveErr.localizedDescription, details: nil))
          return
        }
        // A reload is required after save before the session can be started.
        mgr.loadFromPreferences { loadErr in
          if let loadErr = loadErr {
            result(FlutterError(code: "reload_failed", message: loadErr.localizedDescription, details: nil))
            return
          }
          self.manager = mgr
          self.currentName = name
          self.observeStatus(mgr, name: name)
          do {
            try (mgr.connection as? NETunnelProviderSession)?.startTunnel(options: nil)
            result(nil)
          } catch {
            result(FlutterError(code: "start_failed", message: error.localizedDescription, details: nil))
          }
        }
      }
    }
  }

  private func down(_ result: @escaping FlutterResult) {
    (manager?.connection as? NETunnelProviderSession)?.stopTunnel()
    result(nil)
  }

  /// Configure OS-managed on-demand auto-connect on the (single, reused) tunnel
  /// manager. When enabling with a config, (re)write the protocol so the on-demand
  /// connect has a tunnel to bring up, then set an always-on connect rule — iOS
  /// then brings the tunnel up on any network activity, even with the app not
  /// running. When disabling, clear the rules. The caller is responsible for
  /// disabling on-demand before a manual `down` if it wants the disconnect to
  /// stick (on-demand otherwise re-connects immediately).
  private func setOnDemand(enabled: Bool, name: String?, config: String?,
                           result: @escaping FlutterResult) {
    NETunnelProviderManager.loadAllFromPreferences { [weak self] managers, error in
      guard let self = self else { return }
      if let error = error {
        result(FlutterError(code: "load_failed", message: error.localizedDescription, details: nil))
        return
      }

      let mgr = managers?.first ?? NETunnelProviderManager()

      // Refresh the saved config when enabling and one was supplied, so on-demand
      // brings up the intended profile even if nothing was connected manually.
      if enabled, let name = name, let config = config {
        let proto = NETunnelProviderProtocol()
        proto.providerBundleIdentifier = self.tunnelBundleId
        proto.serverAddress = Self.endpointHost(from: config) ?? "Valenius"
        proto.providerConfiguration = ["wgQuickConfig": config, "name": name]
        mgr.protocolConfiguration = proto
        mgr.localizedDescription = name
        self.currentName = name
      }

      // Can't enable on-demand without a tunnel to bring up.
      if enabled && mgr.protocolConfiguration == nil {
        result(FlutterError(code: "no_config", message: "on-demand needs a saved tunnel config", details: nil))
        return
      }

      mgr.isOnDemandEnabled = enabled
      if enabled {
        // Always-on: a single connect rule with no SSID/interface restriction
        // matches every network, so the OS keeps the VPN up whenever online.
        let rule = NEOnDemandRuleConnect()
        rule.interfaceTypeMatch = .any
        mgr.onDemandRules = [rule]
      } else {
        mgr.onDemandRules = []
      }
      mgr.isEnabled = true

      mgr.saveToPreferences { saveErr in
        if let saveErr = saveErr {
          result(FlutterError(code: "save_failed", message: saveErr.localizedDescription, details: nil))
          return
        }
        self.manager = mgr
        result(nil)
      }
    }
  }

  private func stats(name: String?, result: @escaping FlutterResult) {
    guard let session = manager?.connection as? NETunnelProviderSession,
          name == nil || name == currentName else {
      result(nil)
      return
    }
    do {
      // Round-trips to the extension, which replies with WireGuardKit's runtime
      // config parsed into {lastHandshakeEpochSec, rxBytes, txBytes}.
      try session.sendProviderMessage("stats".data(using: .utf8)!) { reply in
        guard let reply = reply,
              let obj = try? JSONSerialization.jsonObject(with: reply) as? [String: Any] else {
          result(nil)
          return
        }
        result(obj)
      }
    } catch {
      result(nil)
    }
  }

  // MARK: - State events

  private func observeStatus(_ mgr: NETunnelProviderManager, name: String) {
    if let obs = statusObserver { NotificationCenter.default.removeObserver(obs) }
    statusObserver = NotificationCenter.default.addObserver(
      forName: .NEVPNStatusDidChange, object: mgr.connection, queue: .main
    ) { [weak self] _ in
      self?.emit(status: mgr.connection.status, name: name)
    }
    emit(status: mgr.connection.status, name: name)
  }

  private func emit(status: NEVPNStatus, name: String) {
    let value: String
    switch status {
    case .connected:
      value = "connected"
    case .connecting, .reasserting:
      value = "connecting"
    default:
      value = "down"
    }
    eventSink?(["name": name, "state": value])
  }

  // MARK: - FlutterStreamHandler

  public func onListen(withArguments arguments: Any?,
                       eventSink events: @escaping FlutterEventSink) -> FlutterError? {
    eventSink = events
    return nil
  }

  public func onCancel(withArguments arguments: Any?) -> FlutterError? {
    eventSink = nil
    return nil
  }

  // MARK: - Helpers

  /// This device's own local LAN CIDR(s) (e.g. "192.168.1.0/24"), one per non-loopback,
  /// non-tunnel IPv4 adapter address (Wi-Fi `en0`, cellular `pdp_ip*`, etc). Used by the
  /// pre-connect LAN-conflict check. Mirrors the macOS daemon's
  /// TrustedNetworkDetector.localLanCidrs (same getifaddrs technique).
  private static func localLanCidrs() -> [String] {
    var results: [String] = []
    var addrs: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&addrs) == 0 else { return [] }
    defer { freeifaddrs(addrs) }
    var ptr = addrs
    while let cur = ptr {
      defer { ptr = cur.pointee.ifa_next }
      let name = String(cString: cur.pointee.ifa_name)
      if name == "lo0" || name.hasPrefix("utun") { continue }
      guard let sa = cur.pointee.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET) else { continue }
      var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
      guard getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0,
            let ipInt = ipv4ToUInt32(String(cString: host)) else { continue }
      guard let maskSa = cur.pointee.ifa_netmask, maskSa.pointee.sa_family == UInt8(AF_INET) else { continue }
      var maskHost = [CChar](repeating: 0, count: Int(NI_MAXHOST))
      guard getnameinfo(maskSa, socklen_t(maskSa.pointee.sa_len), &maskHost, socklen_t(maskHost.count), nil, 0, NI_NUMERICHOST) == 0,
            let maskInt = ipv4ToUInt32(String(cString: maskHost)) else { continue }
      let prefix = maskInt.nonzeroBitCount
      let network = ipInt & maskInt
      results.append("\(uint32ToIPv4(network))/\(prefix)")
    }
    return results
  }

  private static func ipv4ToUInt32(_ s: String) -> UInt32? {
    let octets = s.split(separator: ".")
    guard octets.count == 4 else { return nil }
    var v: UInt32 = 0
    for o in octets {
      guard let n = UInt32(o), n <= 255 else { return nil }
      v = (v << 8) | n
    }
    return v
  }

  private static func uint32ToIPv4(_ v: UInt32) -> String {
    "\((v >> 24) & 0xFF).\((v >> 16) & 0xFF).\((v >> 8) & 0xFF).\(v & 0xFF)"
  }

  /// Extract the host from the peer `Endpoint = host:port` line, for the
  /// display-only serverAddress. Returns nil if absent.
  private static func endpointHost(from config: String) -> String? {
    for line in config.split(separator: "\n") {
      let parts = line.split(separator: "=", maxSplits: 1)
      guard parts.count == 2,
            parts[0].trimmingCharacters(in: .whitespaces).lowercased() == "endpoint" else {
        continue
      }
      let endpoint = parts[1].trimmingCharacters(in: .whitespaces)
      // Strip the :port (works for host:port and [v6]:port well enough for a label).
      if let colon = endpoint.lastIndex(of: ":") {
        return String(endpoint[..<colon])
      }
      return endpoint
    }
    return nil
  }
}
