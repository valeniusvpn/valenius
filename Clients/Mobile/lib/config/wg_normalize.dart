/// Android's `VpnService.Builder.addRoute` rejects routes whose host bits are set
/// (`checkNonPrefixBytes` → `IllegalArgumentException: Bad address`). wg-quick on
/// Windows/Linux masks routes implicitly, but here we must hand VpnService the
/// network base, so rewrite each IPv4 CIDR in `AllowedIPs` to its masked address
/// (e.g. `10.16.1.1/22` → `10.16.0.0/22`). Non-IPv4 entries pass through.
String normalizeAllowedIps(String config) {
  final lines = config.split('\n');
  for (var i = 0; i < lines.length; i++) {
    final eq = lines[i].indexOf('=');
    if (eq < 0) continue;
    if (lines[i].substring(0, eq).trim().toLowerCase() != 'allowedips') continue;
    final cidrs = lines[i]
        .substring(eq + 1)
        .split(',')
        .map((c) => _maskIpv4Cidr(c.trim()))
        .where((c) => c.isNotEmpty);
    lines[i] = 'AllowedIPs = ${cidrs.join(', ')}';
  }
  return lines.join('\n');
}

/// Per-profile UDP fallback port + trigger window (mirrors `FallbackEndpoint` from the
/// heartbeat) — how long [ConnectionController] waits with no successful verification
/// before it tries [port] once (docs/design/port-443-fallback.md).
class FallbackTarget {
  const FallbackTarget({required this.port, required this.triggerSeconds});

  final int port;
  final int triggerSeconds;
}

/// Rewrites the port in the config's single "Endpoint = host:port" line, keeping the
/// host unchanged — the mobile analogue of the Windows `ConfigManager.PrepareConfigPath`
/// endpoint override. Used only for the port-443 fallback retry: the in-memory config
/// text handed to the tunnel is never written back to the config store, so the stored
/// profile keeps its real endpoint and the next connect always starts on the primary
/// port again.
String rewriteEndpointPort(String config, int port) {
  final lines = config.split('\n');
  for (var i = 0; i < lines.length; i++) {
    final eq = lines[i].indexOf('=');
    if (eq < 0) continue;
    if (lines[i].substring(0, eq).trim().toLowerCase() != 'endpoint') continue;
    final value = lines[i].substring(eq + 1).trim();
    final colon = value.lastIndexOf(':');
    if (colon < 0) continue;
    final host = value.substring(0, colon);
    lines[i] = 'Endpoint = $host:$port';
    break;
  }
  return lines.join('\n');
}

String _maskIpv4Cidr(String cidr) {
  final slash = cidr.indexOf('/');
  if (slash < 0) return cidr;
  final parts = cidr.substring(0, slash).split('.');
  final prefix = int.tryParse(cidr.substring(slash + 1));
  if (parts.length != 4 || prefix == null || prefix < 0 || prefix > 32) {
    return cidr; // not an IPv4 CIDR — leave untouched
  }
  var ip = 0;
  for (final part in parts) {
    final octet = int.tryParse(part);
    if (octet == null || octet < 0 || octet > 255) return cidr;
    ip = (ip << 8) | octet;
  }
  final mask = prefix == 0 ? 0 : (0xFFFFFFFF << (32 - prefix)) & 0xFFFFFFFF;
  ip &= mask;
  return '${(ip >> 24) & 0xFF}.${(ip >> 16) & 0xFF}.${(ip >> 8) & 0xFF}.${ip & 0xFF}/$prefix';
}
