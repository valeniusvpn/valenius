namespace Valenius.Backend.Services;

public interface ICaService
{
    Task EnsureBootstrappedAsync();
    Task<string> GetCaCertPemAsync();
    Task<(string CertPem, string Fingerprint)> SignCsrAsync(string csrPem);
    Task<HttpClient> GetMtlsHttpClientAsync(TimeSpan timeout);

    /// <summary>
    /// Signs arbitrary bytes with the internal CA private key (ECDSA P-256 / SHA-256),
    /// returning an ASN.1 DER signature. Used to authenticate MFA grants to the sidecar,
    /// which verifies with the CA public key it already trusts (from ca.crt).
    /// </summary>
    Task<byte[]> SignDataAsync(byte[] data);
}
