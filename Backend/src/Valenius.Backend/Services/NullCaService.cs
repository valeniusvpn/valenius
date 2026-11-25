namespace Valenius.Backend.Services;

/// <summary>OSS no-op: the internal CA is a Pro feature. Pro replaces this via DI.</summary>
public sealed class NullCaService : ICaService
{
    public Task EnsureBootstrappedAsync() => Task.CompletedTask;
    public Task EnsureBootstrappedAsync(Valenius.Backend.Data.ApplicationDbContext startupDb) => Task.CompletedTask;
    public Task<string> GetCaCertPemAsync() => Task.FromResult(string.Empty);
    public Task<(string CertPem, string Fingerprint)> SignCsrAsync(string csrPem, int customerId)
        => Task.FromResult((string.Empty, string.Empty));
    public Task<HttpClient> GetMtlsHttpClientAsync(int customerId, string sidecarUrl, TimeSpan timeout)
        => Task.FromResult(new HttpClient { Timeout = timeout });
    public Task<byte[]> SignDataAsync(byte[] data)
        => Task.FromResult(Array.Empty<byte>());
}
