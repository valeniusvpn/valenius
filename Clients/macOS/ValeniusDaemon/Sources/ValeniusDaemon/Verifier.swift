// Gateway /health probe — port of Linux's probe_gateway_health / Windows ConnectivityVerifier.
// GET https://<gatewayIp>:<port>/health *through the tunnel*. The sidecar gateway presents a
// cert from the internal CA the client doesn't trust — but the request already travels over the
// authenticated WireGuard tunnel, so certificate verification is intentionally disabled here
// (exactly as the other clients do).

import Foundation

enum Verifier {
    /// Single probe. Returns true on HTTP 200 within `timeout`.
    static func probeGatewayHealth(ip: String, port: Int, timeout: TimeInterval = 4) async -> Bool {
        guard let url = URL(string: "https://\(ip):\(port)/health") else { return false }
        var req = URLRequest(url: url, timeoutInterval: timeout)
        req.httpMethod = "GET"
        let session = URLSession(configuration: .ephemeral, delegate: InsecureTrustDelegate(), delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        do {
            let (_, response) = try await session.data(for: req)
            return (response as? HTTPURLResponse)?.statusCode == 200
        } catch {
            return false
        }
    }

    /// Poll the gateway until it answers 200 or the deadline passes (mirrors _probe_gateway_until).
    static func probeGatewayUntil(ip: String, port: Int, deadline: Date) async -> Bool {
        while Date() < deadline {
            if await probeGatewayHealth(ip: ip, port: port) { return true }
            try? await Task.sleep(nanoseconds: 2 * 1_000_000_000)
        }
        return false
    }
}

/// Accepts any server cert — safe ONLY because the request rides the authenticated WireGuard
/// tunnel (the gateway IP is only reachable through it). Never use for general backend calls.
private final class InsecureTrustDelegate: NSObject, URLSessionDelegate {
    func urlSession(_ session: URLSession,
                    didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
           let trust = challenge.protectionSpace.serverTrust {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.performDefaultHandling, nil)
        }
    }
}
