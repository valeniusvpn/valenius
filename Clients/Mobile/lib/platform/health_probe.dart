import 'dart:io';

/// A sidecar gateway health target, advertised per profile by the backend
/// (`StatusResponse.GatewayProfiles[]`) for transit-unique sidecar VPNs.
class GatewayTarget {
  const GatewayTarget({required this.ip, required this.healthPort});

  final String ip;
  final int healthPort;

  String get healthUrl => 'https://$ip:$healthPort/health';
}

abstract interface class HealthProbe {
  Future<bool> ok(GatewayTarget target);
}

/// Probes the gateway `/health` endpoint **through the tunnel** (the gateway IP
/// is in AllowedIPs, so once the VPN is up the request routes over it). Accepts
/// the sidecar's self-signed cert — we only care that it answers 200.
class HttpHealthProbe implements HealthProbe {
  @override
  Future<bool> ok(GatewayTarget target) async {
    final client = HttpClient();
    client.connectionTimeout = const Duration(seconds: 4);
    client.badCertificateCallback = (_, __, ___) => true;
    try {
      final req = await client.getUrl(Uri.parse(target.healthUrl));
      final resp = await req.close().timeout(const Duration(seconds: 4));
      return resp.statusCode == 200;
    } catch (_) {
      return false;
    } finally {
      client.close(force: true);
    }
  }
}
