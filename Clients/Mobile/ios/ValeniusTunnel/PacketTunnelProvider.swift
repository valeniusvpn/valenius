import NetworkExtension
import WireGuardKit
import os

/// The Valenius WireGuard tunnel — runs inside the Network Extension process
/// (NOT the app process). Started/stopped by the app via NETunnelProviderManager;
/// receives the wg-quick config through `providerConfiguration` and answers stats
/// requests over `handleAppMessage`.
///
/// STAGED FILE — this is not part of the `valenius_vpn` pod. On the Mac, add it to
/// a new "Network Extension → Packet Tunnel Provider" target in the Runner Xcode
/// project and add WireGuardKit (SPM: https://github.com/WireGuard/wireguard-apple)
/// to that target. See README-finish-on-mac.md.
///
/// Mirrors the Android GoBackend plugin's behaviour: single tunnel, aggregate
/// handshake/byte stats summed across peers.
class PacketTunnelProvider: NEPacketTunnelProvider {

  private lazy var adapter: WireGuardAdapter = {
    WireGuardAdapter(with: self) { logLevel, message in
      os_log("%{public}s", log: Self.log, type: .default, message)
    }
  }()

  private static let log = OSLog(subsystem: "com.stranto.valenius.tunnel", category: "wireguard")

  override func startTunnel(options: [String: NSObject]?,
                            completionHandler: @escaping (Error?) -> Void) {
    guard let proto = protocolConfiguration as? NETunnelProviderProtocol,
          let wgQuick = proto.providerConfiguration?["wgQuickConfig"] as? String else {
      completionHandler(PacketTunnelProviderError.missingConfiguration)
      return
    }

    let tunnelConfiguration: TunnelConfiguration
    do {
      tunnelConfiguration = try TunnelConfiguration(fromWgQuickConfig: wgQuick)
    } catch {
      os_log("Invalid wg-quick config: %{public}s", log: Self.log, type: .error, "\(error)")
      completionHandler(PacketTunnelProviderError.invalidConfiguration)
      return
    }

    adapter.start(tunnelConfiguration: tunnelConfiguration) { adapterError in
      if let adapterError = adapterError {
        os_log("Adapter start failed: %{public}s", log: Self.log, type: .error, "\(adapterError)")
      }
      completionHandler(adapterError)
    }
  }

  override func stopTunnel(with reason: NEProviderStopReason,
                           completionHandler: @escaping () -> Void) {
    adapter.stop { _ in completionHandler() }
  }

  /// Answers the app's "stats" provider message with aggregate counters.
  override func handleAppMessage(_ messageData: Data,
                                 completionHandler: ((Data?) -> Void)?) {
    guard let command = String(data: messageData, encoding: .utf8), command == "stats" else {
      completionHandler?(nil)
      return
    }
    adapter.getRuntimeConfiguration { settings in
      let payload = Self.parseStats(settings ?? "")
      completionHandler?(try? JSONSerialization.data(withJSONObject: payload))
    }
  }

  /// Parse WireGuard's uapi-style runtime config: sum rx/tx across peers and take
  /// the newest handshake, exactly like the Android plugin's `stats`.
  private static func parseStats(_ settings: String) -> [String: Any] {
    var rxBytes: Int64 = 0
    var txBytes: Int64 = 0
    var lastHandshake: Int64 = 0
    for line in settings.split(separator: "\n") {
      let kv = line.split(separator: "=", maxSplits: 1)
      guard kv.count == 2 else { continue }
      let key = kv[0].trimmingCharacters(in: .whitespaces)
      let value = Int64(kv[1].trimmingCharacters(in: .whitespaces)) ?? 0
      switch key {
      case "rx_bytes": rxBytes += value
      case "tx_bytes": txBytes += value
      case "last_handshake_time_sec": if value > lastHandshake { lastHandshake = value }
      default: break
      }
    }
    return [
      "lastHandshakeEpochSec": lastHandshake,
      "rxBytes": rxBytes,
      "txBytes": txBytes,
    ]
  }
}

enum PacketTunnelProviderError: Error {
  case missingConfiguration
  case invalidConfiguration
}
