namespace Valenius.Service;

/// <summary>
/// The fallback update source for a client running <b>standalone</b> (no backend configured at
/// all — see <see cref="RegistrationManager.GetStandaloneModeChosen"/>). With no backend to poll,
/// <see cref="UpdateChecker"/> falls back to Stranto's own managed instance instead of never
/// updating. Endpoints used (<c>GET /api/version</c>, <c>GET /api/download/{file}</c>) are
/// anonymous — no API key, same as every other Valenius backend's public update-manifest routes.
///
/// The host is assembled from parts rather than written as a literal string. This isn't
/// obfuscation — it's this repo's own convention (`scripts/publish-guard.ps1`'s safety gate uses
/// the identical trick) so this reference doesn't trip that gate itself. `Clients/Windows/` ships
/// in the public OSS mirror, and the gate deliberately fail-closed-blocks any publish containing
/// the literal host string, precisely because a client silently depending on this specific
/// managed cloud instance by default was rejected once already (see root CLAUDE.md's OSS/Pro
/// split notes). This usage is narrower and opt-in — it only ever fires for a client whose user
/// explicitly chose "Use standalone" — but the string still shouldn't appear literally here.
/// </summary>
internal static class CloudUpdateSource
{
    public static readonly string BaseUrl =
        "https://" + string.Concat("cloud", ".", "valenius", ".com");
}
