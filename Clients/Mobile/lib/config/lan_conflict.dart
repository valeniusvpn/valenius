/// IPv4 CIDR helpers + the pre-connect LAN-conflict check. Before connecting, the app
/// must refuse a profile whose remote routes (or assigned VPN IP) overlap this device's
/// own local LAN — otherwise local devices become unreachable or traffic meant for the
/// remote network silently misroutes to the local LAN instead. IPv4-only, matching every
/// other CIDR-overlap check in this codebase. Mirrors Windows
/// WireGuardController.FindLocalLanConflict / Linux find_local_lan_conflict / macOS
/// ConfigManager.findLocalLanConflict.
library;

/// Parses "ip/prefix" (prefix optional, defaults to 32) into (networkAddress, prefix).
/// Returns null for non-IPv4/unparseable input.
(int, int)? parseCidrV4(String cidr) {
  final slash = cidr.indexOf('/');
  final addrPart = slash < 0 ? cidr : cidr.substring(0, slash);
  final prefix = slash < 0 ? 32 : int.tryParse(cidr.substring(slash + 1));
  if (prefix == null || prefix < 0 || prefix > 32) return null;
  final parts = addrPart.trim().split('.');
  if (parts.length != 4) return null;
  var value = 0;
  for (final part in parts) {
    final octet = int.tryParse(part);
    if (octet == null || octet < 0 || octet > 255) return null;
    value = (value << 8) | octet;
  }
  final mask = prefix == 0 ? 0 : (0xFFFFFFFF << (32 - prefix)) & 0xFFFFFFFF;
  return (value & mask, prefix);
}

/// True if two IPv4 CIDRs overlap (one contains or equals the other).
bool cidrsOverlapV4(String a, String b) {
  final pa = parseCidrV4(a);
  final pb = parseCidrV4(b);
  if (pa == null || pb == null) return false;
  final minPrefix = pa.$2 < pb.$2 ? pa.$2 : pb.$2;
  final mask = minPrefix == 0 ? 0 : (0xFFFFFFFF << (32 - minPrefix)) & 0xFFFFFFFF;
  return (pa.$1 & mask) == (pb.$1 & mask);
}

/// Returns every comma-separated entry across every wg-quick line whose key matches
/// [field] (case-insensitive), trimmed. E.g. `_configField(text, 'AllowedIPs')`.
List<String> _configField(String configText, String field) {
  final lower = field.toLowerCase();
  for (final raw in configText.split('\n')) {
    final line = raw.trim();
    final eq = line.indexOf('=');
    if (eq < 0) continue;
    if (line.substring(0, eq).trim().toLowerCase() != lower) continue;
    return line
        .substring(eq + 1)
        .split(',')
        .map((c) => c.trim())
        .where((c) => c.isNotEmpty)
        .toList();
  }
  return const [];
}

/// Returns a human-readable description of a conflict between [localLanCidrs] (this
/// device's own local LAN) and either (A) an explicit remote route in [configText]'s
/// AllowedIPs, (B) a remote LAN CIDR reported by the backend for a full-tunnel profile
/// ([remoteLanCidrs]), or (C) the VPN IP [configText] would assign to this device — or
/// null if there is none. Default-route AllowedIPs entries (0.0.0.0/0) are excluded from
/// (A): a full tunnel isn't a routable "network" to compare against, and the remote
/// network behind it is only knowable via (B) when the backend can supply it
/// (Valenius-managed sidecar).
String? findLocalLanConflict({
  required String configText,
  required List<String> localLanCidrs,
  required List<String> remoteLanCidrs,
}) {
  if (localLanCidrs.isEmpty) return null;

  final explicitAllowedIps = _configField(configText, 'AllowedIPs')
      .where((c) => !c.startsWith('0.0.0.0/0'));
  final remoteCidrs = [...explicitAllowedIps, ...remoteLanCidrs];
  final assignedAddresses = _configField(configText, 'Address');

  for (final local in localLanCidrs) {
    for (final remote in remoteCidrs) {
      if (cidrsOverlapV4(local, remote)) {
        return 'Your local network ($local) overlaps with a network routed through this VPN '
            '($remote). Connecting would make local devices unreachable or misroute traffic '
            'meant for the remote network. Contact your administrator or change your local '
            'network before connecting.';
      }
    }
    for (final addr in assignedAddresses) {
      if (cidrsOverlapV4(local, addr)) {
        return 'Your local network ($local) overlaps with the VPN address assigned to this '
            'device ($addr). Connecting would make this device unreachable on your local '
            'network. Contact your administrator before connecting.';
      }
    }
  }
  return null;
}
