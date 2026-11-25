using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Valenius.Service;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class DeviceCredentialMigrationTests
{
    private static ApiKeyProvider Provider(string path, Action<string, string>? write = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Valenius:ApiKey"] = "legacy-key" }).Build(),
        NullLogger<ApiKeyProvider>.Instance, path, write);

    [Fact]
    public async Task Failed_persistence_never_confirms_migration_and_success_survives_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "Valenius-key-" + Guid.NewGuid().ToString("N"), "apikey.dat");
        var provider = Provider(path, (_, _) => throw new IOException("synthetic storage failure"));
        provider.Update("individual-key");
        Assert.Equal("legacy-key", provider.Current);
        Assert.False(File.Exists(path));
        var transport = new Capture();
        using (var http = new HttpClient(new ApiKeyHandler(provider) { InnerHandler = transport }))
            using (await http.GetAsync("https://backend.invalid/api/clients/status")) { }
        Assert.Equal("legacy-key", transport.Key);
        Assert.Equal("1", transport.Capability);
        provider = Provider(path);
        try
        {
            provider.Update("individual-key");
            Assert.Equal("individual-key", provider.Current);
            Assert.Equal("individual-key", Provider(path).Current);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!); // Empty, unique test directory only.
        }
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Key, Capability;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Key = request.Headers.GetValues("X-Api-Key").Single();
            Capability = request.Headers.GetValues("X-Valenius-Device-Auth").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
