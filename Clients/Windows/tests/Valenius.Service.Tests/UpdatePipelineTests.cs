using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Valenius.Service;
using Valenius.Shared;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class UpdatePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Valenius-update-pipeline-" + Guid.NewGuid().ToString("N"));
    private string Updates => Path.Combine(_root, "updates");
    private string Stale => Path.Combine(Updates, "old-installer.exe");
    private readonly List<string> _events = [];
    private readonly TestLogger _logger = new();

    private async Task<HttpFixture> Run(string artifact, string version, bool cached)
    {
        Directory.CreateDirectory(Updates);
        File.WriteAllText(Stale, "Old installer must survive a rejected update.");
        var target = Path.Combine(Updates, $"ValeniusSetup-{version}.exe");
        if (cached) File.Copy(artifact, target);
        var manifest = new VersionManifest { Version = version, Sha256 = UpdatePublisherTests.Hash(artifact), DownloadUrl = "https://cdn.invalid/installer.exe" };
        var http = new HttpFixture(manifest, artifact);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Valenius:BackendUrl"] = "https://backend.invalid"
        }).Build();
        var backend = new BackendUrlProvider(configuration, NullLogger<BackendUrlProvider>.Instance, Path.Combine(_root, "backend.dat"));
        var registration = new RegistrationManager(configuration, NullLogger<RegistrationManager>.Instance,
            TimeProvider.System, Path.Combine(_root, "registration.json"));
        using var updater = new UpdateChecker(http, _logger, null!, new TunnelStateManager(), registration, backend, Updates,
            launchInstaller: path =>
            {
                Assert.Equal(target, path);
                Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write).Dispose());
                _events.Add("launch");
            },
            disconnectTunnels: _ => { _events.Add("disconnect"); return Task.CompletedTask; });
        await updater.CheckAndApplyUpdateAsync();
        return http;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectHashUnsignedPayloadCannotDeleteCacheDisconnectOrLaunch(bool cached)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "unsigned.exe");
        File.WriteAllText(path, "An attacker may supply both payload and matching hash.");
        using var http = await Run(path, "9999.0.0", cached);
        Assert.Empty(_events);
        Assert.True(File.Exists(Stale));
        Assert.Equal(cached ? 0 : 1, http.Downloads);
        Assert.Contains(_logger.Errors, error => error.Contains("Authenticode", StringComparison.Ordinal));
    }

    [SignedReleaseFact]
    public async Task ValidSignedCachedReleasePassesBeforeCleanupAndHandoff()
    {
        var fixture = UpdatePublisherTests.Fixtures()[0];
        using var http = await Run(fixture, FileVersionInfo.GetVersionInfo(fixture).ProductVersion!.Trim(), cached: true);
        Assert.Equal(["disconnect", "launch"], _events);
        Assert.False(File.Exists(Stale));
        Assert.Equal(0, http.Downloads);
        Assert.Empty(_logger.Errors);
    }

    [SignedReleaseFact]
    public async Task ValidSignedDownloadedReleasePassesSameGate()
    {
        var fixture = UpdatePublisherTests.Fixtures()[1];
        using var http = await Run(fixture, FileVersionInfo.GetVersionInfo(fixture).ProductVersion!.Trim(), cached: false);
        Assert.Equal(["disconnect", "launch"], _events);
        Assert.False(File.Exists(Stale));
        Assert.Equal(1, http.Downloads);
        Assert.Empty(_logger.Errors);
    }

    [SignedReleaseFact]
    public async Task BackendCannotRelabelOldSignedPayloadAsNewVersion()
    {
        using var http = await Run(UpdatePublisherTests.Fixtures()[0], "9999.0.0", cached: true);
        Assert.Empty(_events);
        Assert.True(File.Exists(Stale));
        Assert.Contains(_logger.Errors, error => error.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherTrustedPublisherCannotTriggerPrivilegedActions()
    {
        var fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        using var http = await Run(fixture, "9999.0.0", cached: false);
        Assert.Empty(_events);
        Assert.True(File.Exists(Stale));
        Assert.Contains(_logger.Errors, error => error.Contains("publisher profile", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyingPublisherNameAndProfileEkuDoesNotReplacePinnedAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + UpdatePublisherVerifier.Publisher, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.3"), new(UpdatePublisherVerifier.ProfileEku)
        }, false));
        using var fake = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(UpdatePublisherVerifier.MatchesPublisher(fake, fake));
    }

    private sealed class HttpFixture(VersionManifest manifest, string artifact) : HttpMessageHandler, IHttpClientFactory
    {
        private HttpClient? _client;
        internal int Downloads { get; private set; }
        public HttpClient CreateClient(string name) => _client ??= new HttpClient(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/version")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(manifest) });
            Assert.Equal("https://cdn.invalid/installer.exe", request.RequestUri.AbsoluteUri);
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(artifact)) });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _client?.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TestLogger : ILogger<UpdateChecker>
    {
        internal List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Error) Errors.Add(exception?.Message ?? formatter(state, exception));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
