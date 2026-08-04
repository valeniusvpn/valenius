import 'package:flutter_test/flutter_test.dart';
import 'package:valenius_mobile/config/wg_normalize.dart';

void main() {
  test('masks host bits in AllowedIPs routes', () {
    const config = '[Interface]\n'
        'Address = 172.16.0.7/32\n'
        'DNS = 10.16.1.254\n'
        '\n'
        '[Peer]\n'
        'AllowedIPs = 172.16.0.1/32, 10.16.1.1/22\n'
        'PersistentKeepalive = 25\n';

    final out = normalizeAllowedIps(config);

    expect(out, contains('AllowedIPs = 172.16.0.1/32, 10.16.0.0/22'));
    // Untouched lines stay intact.
    expect(out, contains('Address = 172.16.0.7/32'));
    expect(out, contains('DNS = 10.16.1.254'));
    expect(out, contains('PersistentKeepalive = 25'));
  });

  test('handles full-tunnel and already-masked routes', () {
    expect(
      normalizeAllowedIps('AllowedIPs = 0.0.0.0/0'),
      'AllowedIPs = 0.0.0.0/0',
    );
    expect(
      normalizeAllowedIps('AllowedIPs = 10.0.0.0/8, 192.168.1.0/24'),
      'AllowedIPs = 10.0.0.0/8, 192.168.1.0/24',
    );
  });

  test('leaves non-IPv4 entries untouched', () {
    expect(
      normalizeAllowedIps('AllowedIPs = ::/0, fd00::1/64'),
      'AllowedIPs = ::/0, fd00::1/64',
    );
  });

  test('rewriteEndpointPort swaps the port, keeps the host, leaves the rest alone', () {
    const config = '[Interface]\n'
        'Address = 172.16.0.7/32\n'
        '\n'
        '[Peer]\n'
        'Endpoint = vpn.example.com:51820\n'
        'AllowedIPs = 0.0.0.0/0\n'
        'PersistentKeepalive = 25\n';

    final out = rewriteEndpointPort(config, 443);

    expect(out, contains('Endpoint = vpn.example.com:443'));
    expect(out, isNot(contains(':51820')));
    expect(out, contains('AllowedIPs = 0.0.0.0/0'));
    expect(out, contains('PersistentKeepalive = 25'));
  });

  test('rewriteEndpointPort works with an IPv4 endpoint host', () {
    expect(
      rewriteEndpointPort('Endpoint = 203.0.113.5:51820', 443),
      'Endpoint = 203.0.113.5:443',
    );
  });

  test('rewriteEndpointPort is a no-op when there is no Endpoint line', () {
    const config = '[Interface]\nAddress = 172.16.0.7/32\n';
    expect(rewriteEndpointPort(config, 443), config);
  });
}
