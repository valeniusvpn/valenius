namespace Valenius.Backend.Services;

/// <summary>OSS no-op: sidecar connectivity requires the Pro edition. Pro replaces this via DI.</summary>
public sealed class NullSidecarService : ISidecarService
{
    public Task<ServerInfo?> GetInfoAsync(string sidecarUrl, int customerId = 0)
        => Task.FromResult<ServerInfo?>(null);

    public Task<List<PeerStats>?> GetPeersAsync(string sidecarUrl)
        => Task.FromResult<List<PeerStats>?>(null);

    public Task<(bool Ok, string Message, ServerInfo? Info)> TestConnectionAsync(
        string sidecarUrl, int customerId = 0)
        => Task.FromResult((false, "Sidecar integration requires the Pro edition.", (ServerInfo?)null));

    public Task<bool> AddPeerAsync(string sidecarUrl, string publicKey, string allowedIp)
        => Task.FromResult(false);

    public Task<bool> RemovePeerAsync(string sidecarUrl, string publicKey)
        => Task.FromResult(false);

    public Task<bool> PushGrantAsync(string sidecarUrl, string grantJson, string signatureBase64)
        => Task.FromResult(false);

    public Task<bool> RevokeGrantAsync(string sidecarUrl, Guid sessionId)
        => Task.FromResult(false);

    public Task<List<SidecarGrantInfo>?> GetGrantsAsync(string sidecarUrl)
        => Task.FromResult<List<SidecarGrantInfo>?>(null);
}
