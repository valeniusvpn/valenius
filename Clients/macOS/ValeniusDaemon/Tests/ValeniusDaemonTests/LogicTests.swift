// Offline unit tests for the pure, error-prone logic that can't be exercised without a live
// WireGuard peer: CIDR/trusted-network math, the config-content allowlist, and .conf → UAPI
// translation (base64 key → hex). These lock down the parts most likely to break silently.

import XCTest
@testable import ValeniusDaemon

final class LogicTests: XCTestCase {

    // MARK: CIDR overlap (multi-VPN conflict + trusted networks share this math)

    func testCidrOverlap() {
        XCTAssertTrue(cidrsOverlapV4("10.0.0.0/24", "10.0.0.128/25"))
        XCTAssertTrue(cidrsOverlapV4("10.0.0.5/32", "10.0.0.0/24"))
        XCTAssertFalse(cidrsOverlapV4("10.0.0.0/24", "10.0.1.0/24"))
        XCTAssertTrue(cidrsOverlapV4("0.0.0.0/0", "8.8.8.8/32"))
        XCTAssertFalse(cidrsOverlapV4("192.168.1.0/24", "192.168.2.0/24"))
    }

    func testTrustedNetworkMatch() {
        // A subnet with no local IP in it is never trusted.
        XCTAssertFalse(TrustedNetworkDetector.isInTrustedNetwork([
            TrustedNetwork(subnet: "203.0.113.0/24", primaryDns: nil)
        ]))
        // Empty list is never trusted.
        XCTAssertFalse(TrustedNetworkDetector.isInTrustedNetwork([]))
        // A /0 subnet matches any local IPv4 (when the host has at least one).
        let hasLocal = !TrustedNetworkDetector.localIPv4Addresses().isEmpty
        XCTAssertEqual(TrustedNetworkDetector.isInTrustedNetwork([
            TrustedNetwork(subnet: "0.0.0.0/0", primaryDns: nil)
        ]), hasLocal)
    }

    // MARK: Config content allowlist (local-user → root guard)

    func testAllowlistAcceptsCleanConfig() {
        let ok = """
        [Interface]
        PrivateKey = aGVsbG8=
        Address = 10.0.0.2/32
        DNS = 10.0.0.1

        [Peer]
        PublicKey = d29ybGQ=
        Endpoint = vpn.example.com:51820
        AllowedIPs = 10.0.0.0/24
        PersistentKeepalive = 25
        """
        XCTAssertNil(ConfigValidation.validateContent(ok))
    }

    func testAllowlistRejectsScriptHooks() {
        let evil = """
        [Interface]
        PrivateKey = aGVsbG8=
        PostUp = rm -rf /

        [Peer]
        PublicKey = d29ybGQ=
        AllowedIPs = 0.0.0.0/0
        """
        XCTAssertNotNil(ConfigValidation.validateContent(evil))
    }

    func testAllowlistRejectsUnknownSection() {
        XCTAssertNotNil(ConfigValidation.validateContent("[Malicious]\nfoo = bar"))
    }

    // MARK: .conf → UAPI translation

    func testUapiSerializationConvertsKeysToHex() throws {
        // 32 zero bytes, base64 → hex is 64 zeros.
        let zeroKey = Data(repeating: 0, count: 32).base64EncodedString()
        let conf = """
        [Interface]
        PrivateKey = \(zeroKey)
        Address = 10.0.0.2/32

        [Peer]
        PublicKey = \(zeroKey)
        Endpoint = 1.2.3.4:51820
        AllowedIPs = 0.0.0.0/0
        PersistentKeepalive = 25
        """
        let cfg = try WireGuardConfig.parse(conf)
        let uapi = try cfg.uapiSetRequest()
        XCTAssertTrue(uapi.hasPrefix("set=1\n"))
        XCTAssertTrue(uapi.contains("private_key=" + String(repeating: "0", count: 64)))
        XCTAssertTrue(uapi.contains("public_key=" + String(repeating: "0", count: 64)))
        XCTAssertTrue(uapi.contains("endpoint=1.2.3.4:51820"))
        XCTAssertTrue(uapi.contains("persistent_keepalive_interval=25"))
        XCTAssertTrue(uapi.contains("allowed_ip=0.0.0.0/0"))
    }

    func testParseRejectsBadKey() {
        let conf = """
        [Interface]
        PrivateKey = not-base64!!

        [Peer]
        PublicKey = also-bad
        AllowedIPs = 10.0.0.0/24
        """
        // Parse succeeds (allowlist already ran upstream); UAPI serialization catches the bad key.
        let cfg = try? WireGuardConfig.parse(conf)
        XCTAssertNotNil(cfg)
        XCTAssertThrowsError(try cfg!.uapiSetRequest())
    }

    // MARK: Version comparison (auto-update gate)

    func testVersionCompare() {
        XCTAssertTrue(versionGreater("0.2.1", than: "0.1.9"))   // minor beats patch
        XCTAssertTrue(versionGreater("1.0.0", than: "0.9.9"))
        XCTAssertTrue(versionGreater("0.1.10", than: "0.1.9"))  // numeric, not lexical
        XCTAssertFalse(versionGreater("0.1.0", than: "0.1.0"))  // equal → no update
        XCTAssertFalse(versionGreater("0.1.0", than: "0.2.0"))  // older → no update
        XCTAssertTrue(versionGreater("0.2", than: "0.1.9"))     // uneven part counts
    }

    // MARK: Diagnostics redaction (secrets must never leave the machine)

    func testRedactionStripsSecrets() {
        let apiKey = "25f5ce51137b3a457577042df19376a3"
        let raw = """
        heartbeat authenticated with key \(apiKey) ok
        PrivateKey = qMByh5abcdefghijklmnopqrstuvwxyz0123456789A=
        PresharedKey = ABCdefGHIjklMNOpqrsTUVwxyz0123456789abcdEF0=
        secret: hunter2supersecret
        Authorization: Bearer abc.def.ghi
        Endpoint = 1.2.3.4:51820
        """
        let out = Diagnostics.redact(raw, apiKey: apiKey)
        // The security-critical guarantee: no secret survives into the bundle.
        XCTAssertFalse(out.contains(apiKey), "API key leaked")
        XCTAssertFalse(out.contains("qMByh5abcdefghijklmnopqrstuvwxyz0123456789A="), "PrivateKey leaked")
        XCTAssertFalse(out.contains("ABCdefGHIjklMNOpqrsTUVwxyz0123456789abcdEF0="), "PresharedKey leaked")
        XCTAssertFalse(out.contains("hunter2supersecret"), "secret value leaked")
        XCTAssertFalse(out.contains("abc.def.ghi"), "bearer token leaked")
        XCTAssertTrue(out.contains("[REDACTED-APIKEY]"), "api key not marked redacted")
        // Non-secret operational data is preserved for diagnostics.
        XCTAssertTrue(out.contains("Endpoint = 1.2.3.4:51820"))
    }

    // MARK: DNS split (nameservers vs search domains)

    func testDnsSplit() throws {
        let conf = """
        [Interface]
        PrivateKey = \(Data(repeating: 1, count: 32).base64EncodedString())
        Address = 10.0.0.2/32
        DNS = 10.0.0.1, corp.example.com

        [Peer]
        PublicKey = \(Data(repeating: 2, count: 32).base64EncodedString())
        AllowedIPs = 10.0.0.0/24
        """
        let cfg = try WireGuardConfig.parse(conf)
        XCTAssertEqual(cfg.nameservers, ["10.0.0.1"])
        XCTAssertEqual(cfg.searchDomains, ["corp.example.com"])
    }
}
