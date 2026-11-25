using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Valenius.Service;
using Valenius.Shared;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class OfflineAccessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Valenius-offline-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string StatePath => Path.Combine(_directory, "registration.json");

    private static IConfiguration Configuration(string? days = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Valenius:OfflineAccessDays"] = days,
            ["Valenius:BackendUrl"] = "https://offline-tests.invalid"
        }).Build();

    private RegistrationManager Manager(string? days = null, Action<string, string>? writeState = null) =>
        new(Configuration(days), NullLogger<RegistrationManager>.Instance, _clock, StatePath, writeState);

    private async Task<RegistrationManager> Activate(string? days = null, ClientStatusResponse? status = null)
    {
        var manager = Manager(days);
        await manager.GetOrCreateClientIdAsync();
        await manager.UpdateAsync(status ?? new ClientStatusResponse(IsActive: true));
        return manager;
    }

    [Fact]
    public async Task ApprovedClientCanReconnectAfterMoreThanOneDayAndAfterRestart()
    {
        var manager = await Activate();
        var identity = manager.GetCachedClientId();
        var expectedExpiry = _clock.GetUtcNow().UtcDateTime.AddDays(7);
        _clock.Advance(TimeSpan.FromDays(6));
        manager.SetBackendReachable(false);

        Assert.True(await manager.IsAllowedToConnectAsync());
        var restarted = Manager();
        Assert.True(await restarted.IsAllowedToConnectAsync());
        Assert.Equal(identity, await restarted.GetOrCreateClientIdAsync());
        Assert.Equal(expectedExpiry, restarted.GetOfflineAccessExpiresAt());
        Assert.Null(restarted.GetBackendReachable());
    }

    [Fact]
    public async Task LeaseExpiresAtSevenDaysAndRestartDoesNotExtendIt()
    {
        var manager = await Activate();
        _clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromTicks(1));
        Assert.True(await manager.IsAllowedToConnectAsync());
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(await manager.IsAllowedToConnectAsync());
        Assert.False(await Manager().IsAllowedToConnectAsync());
        Assert.Contains("expired", manager.GetConnectionDeniedReason());
    }

    [Fact]
    public async Task AdministratorCanConfigureThirtyDays()
    {
        var manager = await Activate("30");
        _clock.Advance(TimeSpan.FromDays(8));
        Assert.True(await manager.IsAllowedToConnectAsync());
        Assert.True(await Manager("30").IsAllowedToConnectAsync());
        _clock.Advance(TimeSpan.FromDays(22));
        Assert.False(await manager.IsAllowedToConnectAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("366")]
    [InlineData("invalid")]
    public void InvalidOfflinePeriodFailsInsteadOfGrantingIndefiniteAccess(string days) =>
        Assert.Throws<InvalidOperationException>(() => Manager(days));

    [Fact]
    public async Task NeverApprovedAndExplicitlyDeactivatedClientsCannotConnect()
    {
        var manager = Manager();
        Assert.False(await manager.IsAllowedToConnectAsync());
        await manager.GetOrCreateClientIdAsync();
        Assert.False(await manager.IsAllowedToConnectAsync());
        await manager.UpdateAsync(new ClientStatusResponse(IsActive: true));
        await manager.UpdateAsync(new ClientStatusResponse(IsActive: false));
        manager.SetBackendReachable(false);
        Assert.False(await manager.IsAllowedToConnectAsync());
        Assert.False(await Manager().IsAllowedToConnectAsync());
        Assert.Null(manager.GetOfflineAccessExpiresAt());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LatestBackendDecisionRemainsAuthoritativeWhenPersistenceFails(bool isActive)
    {
        await Activate();
        var manager = Manager(writeState: (_, _) => throw new UnauthorizedAccessException("Test storage failure"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.UpdateAsync(new ClientStatusResponse(isActive)));
        Assert.Equal(isActive, await manager.IsAllowedToConnectAsync());
        Assert.Equal(isActive, manager.GetCachedIsActive());
    }

    [Fact]
    public async Task LegacyCacheStillWorksWithoutAConnectionPolicySnapshot()
    {
        Directory.CreateDirectory(_directory);
        var identity = Guid.NewGuid();
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new RegistrationState
        {
            ClientId = identity,
            IsActive = true,
            LastCheckedUtc = _clock.GetUtcNow().UtcDateTime.AddDays(-2)
        }, ServiceJsonContext.Default.RegistrationState));

        var manager = Manager();
        Assert.True(await manager.IsAllowedToConnectAsync());
        Assert.Equal(identity, await manager.GetOrCreateClientIdAsync());
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task CorruptOrInvalidRegistrationDoesNotCreateANewIdentity(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, content);
        var manager = Manager();
        Assert.False(await manager.IsAllowedToConnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrCreateClientIdAsync());
        Assert.Equal(content, File.ReadAllText(StatePath));
    }

    [Fact]
    public async Task UnreadableCacheCanRecoverWithoutReplacingIdentity()
    {
        var original = await Activate();
        var manager = Manager();
        using (var heldFile = new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await manager.IsAllowedToConnectAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetOrCreateClientIdAsync());
        }
        Assert.True(await manager.IsAllowedToConnectAsync());
        Assert.Equal(original.GetCachedClientId(), manager.GetCachedClientId());
    }

    [Fact]
    public async Task PolicySurvivesRestartWithoutPersistingCredentialsOrPendingActions()
    {
        var status = new ClientStatusResponse(IsActive: true,
            ServerProfileName: "office", ServerHealthUrl: "https://vpn.invalid:9004/health", ServerVpnIp: "10.77.0.1",
            DeviceTunnelEnabled: true, SkipConnectivityCheck: false, UpdateChannel: "beta",
            GatewayProfiles: [new("office", "10.77.0.1", 9004)],
            FallbackEndpoints: [new("office", 443, 20)],
            RemoteLanCidrsByProfile: [new("office", ["10.16.0.0/24"])],
            MfaRequired: true, MfaAuthorizeUrl: "https://backend.invalid/mfa", MfaEnrollmentOpen: true,
            MfaEnrollmentUri: "test-enrollment-secret", MfaApproveNumber: 42,
            ClientApiKey: "test-api-key", PendingConnect: true, ForceUpdate: true,
            PendingForeignConfigs: [new("secret-profile", "test-private-config")]);
        await Activate(status: status);
        var restarted = Manager();

        Assert.Equal("office", restarted.GetServerProfileName());
        Assert.True(restarted.GetDeviceTunnelEnabled());
        Assert.Equal(("10.77.0.1", 9004), restarted.GetGatewayProbe("office"));
        Assert.Equal((443, 20), restarted.GetFallbackConfig("office"));
        Assert.Equal(["10.16.0.0/24"], restarted.GetRemoteLanCidrs("office"));
        Assert.Equal("beta", restarted.GetUpdateChannel());
        Assert.True(restarted.GetMfaRequired());
        Assert.True(restarted.GetMfaEnrollmentOpen());
        Assert.Null(restarted.GetMfaEnrollmentUri());
        Assert.Null(restarted.GetMfaApproveNumber());

        var json = File.ReadAllText(StatePath);
        foreach (var secret in new[] { "10.77.0.1", "test-enrollment-secret", "test-api-key", "test-private-config", "PendingConnect", "ForceUpdate" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public async Task CachedActivationDoesNotExtendAnMfaSession()
    {
        var manager = await Activate(status: new ClientStatusResponse(IsActive: true,
            MfaSessionExpiresAt: _clock.GetUtcNow().UtcDateTime.AddHours(1)));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.True(await manager.IsAllowedToConnectAsync());
        Assert.True(manager.GetMfaRequired());
        Assert.True(Manager().GetMfaRequired());
    }

    [Fact]
    public async Task CorruptProtectedPolicyRequiresRefreshButPreservesIdentity()
    {
        var manager = await Activate();
        var state = JsonSerializer.Deserialize(File.ReadAllText(StatePath), ServiceJsonContext.Default.RegistrationState)!;
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state with { ProtectedConnectionPolicy = "invalid" }, ServiceJsonContext.Default.RegistrationState));
        var restarted = Manager();
        Assert.False(await restarted.IsAllowedToConnectAsync());
        Assert.Equal(manager.GetCachedClientId(), await restarted.GetOrCreateClientIdAsync());
        await restarted.UpdateAsync(new ClientStatusResponse(IsActive: true));
        Assert.True(await restarted.IsAllowedToConnectAsync());
    }

    [Fact]
    public async Task BackwardsClockJumpDoesNotExtendAuthorization()
    {
        var manager = await Activate();
        _clock.Advance(TimeSpan.FromDays(-1));
        Assert.False(await manager.IsAllowedToConnectAsync());
    }

    [Fact]
    public async Task StandaloneChoiceCanBeSavedBeforeTheFirstHeartbeat()
    {
        var manager = Manager();
        await manager.SetStandaloneModeAsync(true);
        Assert.True(Manager().GetStandaloneModeChosen());
        Assert.NotEqual(Guid.Empty, manager.GetCachedClientId());
        Assert.False(await manager.IsAllowedToConnectAsync());
    }

    [Fact]
    public async Task SuccessfulRefreshRestoresExpiredAccessWithoutChangingIdentity()
    {
        var manager = await Activate();
        var identity = manager.GetCachedClientId();
        _clock.Advance(TimeSpan.FromDays(8));
        Assert.False(await manager.IsAllowedToConnectAsync());
        await manager.UpdateAsync(new ClientStatusResponse(IsActive: true));
        Assert.True(await Manager().IsAllowedToConnectAsync());
        Assert.Equal(identity, manager.GetCachedClientId());
    }

    [Fact]
    public void FailedAtomicReplacementKeepsThePreviousCompleteCache()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, "previous-complete-state");
        using (var heldFile = new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Record.Exception(() => StateFileWriter.WriteAllTextAtomic(StatePath, "replacement-state"));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected a sharing/access failure, received {error?.GetType().Name ?? "no error"}.");
        }
        Assert.Equal("previous-complete-state", File.ReadAllText(StatePath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void OfflineStatusFieldsRoundTripAndRemainOptionalForOlderServices()
    {
        var status = new TunnelStatus { BackendReachable = false, OfflineAccessExpiresAt = _clock.GetUtcNow().UtcDateTime.AddDays(7) };
        var json = JsonSerializer.Serialize(status, ValeniusJsonContext.Default.TunnelStatus);
        var roundTrip = JsonSerializer.Deserialize(json, ValeniusJsonContext.Default.TunnelStatus)!;
        Assert.False(roundTrip.BackendReachable);
        Assert.Equal(status.OfflineAccessExpiresAt, roundTrip.OfflineAccessExpiresAt);
        var legacyStatus = JsonSerializer.Deserialize("{}", ValeniusJsonContext.Default.TunnelStatus)!;
        Assert.Null(legacyStatus.BackendReachable);
        Assert.Null(legacyStatus.OfflineAccessExpiresAt);
    }

    [Fact]
    public async Task HeartbeatTimeoutUsesCachedAccessInsteadOfStoppingTheService()
    {
        var manager = await Activate();
        _clock.Advance(TimeSpan.FromDays(2));
        var service = Service(manager, new TimeoutFactory(), NullLogger<ClientRegistrationService>.Instance);
        var result = await service.TryRegisterAsync();
        Assert.Null(result.IsActive);
        Assert.False(manager.GetBackendReachable());
        Assert.True(await manager.IsAllowedToConnectAsync());
    }

    [Fact]
    public async Task LongPollTimeoutStaysInTheRetryLoopUntilShutdown()
    {
        var manager = await Activate();
        var logger = new RetryLogger();
        var service = Service(manager, new TimeoutFactory(), logger);
        using var shutdown = new CancellationTokenSource();
        var loop = service.RunLongPollLoopAsync(shutdown.Token);
        try
        {
            await logger.Retried.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(loop.IsCompleted);
            Assert.False(manager.GetBackendReachable());
            Assert.True(await manager.IsAllowedToConnectAsync());
        }
        finally { shutdown.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task ShutdownCancellationIsNotTreatedAsAnOutage()
    {
        var manager = await Activate();
        var service = Service(manager, new TimeoutFactory(), NullLogger<ClientRegistrationService>.Instance);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TryRegisterAsync(ct: shutdown.Token));
        Assert.True(manager.GetBackendReachable());
    }

    [Fact]
    public async Task OptionalPublicIpLookupCannotWaitForTheDefaultHttpTimeout()
    {
        var manager = await Activate();
        using var factory = new HangingHttpFactory();
        var service = Service(manager, factory, NullLogger<ClientRegistrationService>.Instance);
        var result = await service.GetWanIpAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(result);
        Assert.True(factory.RequestCancelled);
    }

    private static ClientRegistrationService Service(RegistrationManager manager, IHttpClientFactory factory,
        ILogger<ClientRegistrationService> logger) => new(factory, manager, null!, null!, null!, null!, null!, null!, null!,
            new BackendUrlProvider(Configuration(), NullLogger<BackendUrlProvider>.Instance), null!, logger);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    // Throw before any real network call or Windows tunnel/configuration operation.
    private sealed class TimeoutFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new TaskCanceledException("Simulated HTTP timeout");
    }

    private sealed class HangingHttpFactory : HttpMessageHandler, IHttpClientFactory
    {
        public bool RequestCancelled { get; private set; }
        private readonly HttpClient _client;
        public HangingHttpFactory() => _client = new HttpClient(this, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { RequestCancelled = true; throw; }
            throw new InvalidOperationException("The simulated request should only complete by cancellation.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _client.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RetryLogger : ILogger<ClientRegistrationService>
    {
        public TaskCompletionSource Retried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Long poll failed;", StringComparison.Ordinal)) Retried.TrySetResult();
        }
    }
}
