namespace Valenius.Service;

/// <summary>
/// Injects the current <c>X-Api-Key</c> on every backend request, reading the live value from
/// <see cref="ApiKeyProvider"/> so a runtime key rotation takes effect on the very next request
/// without re-creating the HttpClient. Replaces the old baked-at-startup default header.
/// </summary>
public sealed class ApiKeyHandler(ApiKeyProvider keys) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = keys.Current;
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Remove("X-Api-Key");
            request.Headers.Add("X-Api-Key", key);
        }
        return base.SendAsync(request, ct);
    }
}
