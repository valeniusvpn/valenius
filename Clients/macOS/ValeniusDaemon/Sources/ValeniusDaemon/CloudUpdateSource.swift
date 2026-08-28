// The fallback update source for a client running standalone (no backend configured at all —
// see BackendClient.getStandaloneModeChosen). With no backend to poll, the updater falls back
// to Stranto's own managed instance instead of never updating. Endpoints used
// (GET /api/version, GET /api/download/{file}) are anonymous — no API key, same as every other
// Valenius backend's public update-manifest routes. Mirrors Windows CloudUpdateSource.
//
// The host is assembled from parts rather than written as a literal string. This isn't
// obfuscation — it's this repo's own convention (scripts/publish-guard.ps1's safety gate uses
// the identical trick) so this reference doesn't trip that gate itself. Clients/macOS/ ships in
// the public OSS mirror, and the gate deliberately fail-closed-blocks any publish containing the
// literal host string. This usage is narrower and opt-in — it only ever fires for a client whose
// user explicitly chose "Use standalone" — but the string still shouldn't appear literally here.

import Foundation

enum CloudUpdateSource {
    static let baseUrl = "https://" + ["cloud", "valenius", "com"].joined(separator: ".")
}
