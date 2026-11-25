namespace Valenius.Backend.Services;

/// <summary>OSS no-op: sidecar connectivity requires the Pro edition. Pro replaces this via DI.</summary>
public sealed class NullSidecarService : ISidecarService
{
    public Task<ServerInfo?> GetInfoAsync(string sidecarUrl, int customerId, bool cacheInfo = true)
        => Task.FromResult<ServerInfo?>(null);

    public Task<List<PeerStats>?> GetPeersAsync(string sidecarUrl, int customerId)
        => Task.FromResult<List<PeerStats>?>(null);

    public Task<(bool Ok, string Message, ServerInfo? Info)> TestConnectionAsync(
        string sidecarUrl, int customerId)
        => Task.FromResult((false, "Sidecar integration requires the Pro edition.", (ServerInfo?)null));

    public Task<bool> AddPeerAsync(string sidecarUrl, int customerId, string publicKey, string allowedIp)
        => Task.FromResult(false);

    public Task<bool> RemovePeerAsync(string sidecarUrl, int customerId, string publicKey)
        => Task.FromResult(false);

    public Task<(bool Ok, string Message, bool Active)> SetFallbackAsync(
        string sidecarUrl, int customerId, bool enabled, int port)
        => Task.FromResult((false, "Sidecar integration requires the Pro edition.", false));

    public Task<SidecarSelfTest?> GetSelfTestAsync(string sidecarUrl, int customerId)
        => Task.FromResult<SidecarSelfTest?>(null);

    public Task<bool> PushGrantAsync(string sidecarUrl, int customerId, string grantJson, string signatureBase64)
        => Task.FromResult(false);

    public Task<bool> RevokeGrantAsync(string sidecarUrl, int customerId, Guid sessionId)
        => Task.FromResult(false);

    public Task<List<SidecarGrantInfo>?> GetGrantsAsync(string sidecarUrl, int customerId)
        => Task.FromResult<List<SidecarGrantInfo>?>(null);
}
