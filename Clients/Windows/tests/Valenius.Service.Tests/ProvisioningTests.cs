using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Valenius.Service;
using Valenius.Shared;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class ProvisioningTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Valenius-provisioning-" + Guid.NewGuid().ToString("N"));
    private string UrlPath => Path.Combine(_directory, "backend.dat");
    private BackendUrlProvider Provider(string initial = "", Action<string, string>? write = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Valenius:BackendUrl"] = initial }).Build(),
        NullLogger<BackendUrlProvider>.Instance, UrlPath, write);

    private ClientRegistrationService RegistrationService(BackendUrlProvider provider, RegistrationManager registration) =>
        new(null!, registration, null!, null!, null!, null!, null!, null!, null!, provider, null!,
            NullLogger<ClientRegistrationService>.Instance);

    private RegistrationManager Registration() => new(new ConfigurationBuilder().Build(),
        NullLogger<RegistrationManager>.Instance, TimeProvider.System, Path.Combine(_directory, "registration.json"));

    [Fact]
    public async Task ServiceEntryPointsAlsoRejectUnprivilegedProvisioning()
    {
        var provider = Provider();
        var service = RegistrationService(provider, Registration());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetBackendUrlAsync("attacker.invalid"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetStandaloneModeAsync(false));
        Assert.False(provider.IsConfigured);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task AdministratorCanChooseStandaloneOnlyWhenNoBackendExists()
    {
        var registration = Registration();
        await RegistrationService(Provider(), registration).SetStandaloneModeAsync(true);
        Assert.True(registration.GetStandaloneModeChosen());
        var before = File.ReadAllBytes(Path.Combine(_directory, "registration.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RegistrationService(Provider("https://original.invalid"), registration).SetStandaloneModeAsync(true));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_directory, "registration.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://original.invalid")]
    public void OrdinaryCallerCannotEnrollOrReassignEvenBeforeFirstRun(string initial)
    {
        var provider = Provider(initial);
        Assert.Throws<UnauthorizedAccessException>(() => provider.Set("attacker.invalid"));
        Assert.Equal(initial, provider.Current);
        Assert.False(File.Exists(UrlPath));
    }

    [Fact]
    public void AdministratorInitialEnrollmentPersistsAndExistingOriginCannotBeReassigned()
    {
        var provider = Provider();
        Assert.Equal("https://original.invalid", provider.Set("original.invalid", privileged: true));
        var before = File.ReadAllBytes(UrlPath);
        Assert.Throws<InvalidOperationException>(() => provider.Set("attacker.invalid", privileged: true));
        Assert.Equal(before, File.ReadAllBytes(UrlPath));
        Assert.Equal("https://original.invalid", Provider().Current);
    }

    [Fact]
    public void InstallerProvisionedOriginCannotBeReplaced()
    {
        var provider = Provider("https://original.invalid");
        Assert.Throws<InvalidOperationException>(() => provider.Set("attacker.invalid", privileged: true));
        Assert.False(File.Exists(UrlPath));
    }

    [Fact]
    public void FailedPersistenceCannotSelectAnEphemeralUpdateOrigin()
    {
        var provider = Provider(write: (_, _) => throw new IOException("injected write failure"));
        Assert.Throws<IOException>(() => provider.Set("original.invalid", privileged: true));
        Assert.False(provider.IsConfigured);
        Assert.False(File.Exists(UrlPath));
    }

    [Fact]
    public async Task ConcurrentAdministratorEnrollmentsCommitOnlyOneOrigin()
    {
        var provider = Provider();
        var tasks = new[] { "first.invalid", "second.invalid" }.Select(host => Task.Run(() =>
        {
            try { return provider.Set(host, privileged: true) != null; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Single(await Task.WhenAll(tasks), success => success);
        Assert.Equal(provider.Current, Provider().Current);
    }

    [Fact]
    public void DamagedSavedOriginCannotFallBackToAnotherInstallerOrigin()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(UrlPath, "");
        Assert.Throws<IOException>(() => Provider("https://different.invalid"));
        File.Delete(UrlPath);
        Directory.CreateDirectory(UrlPath);
        Assert.Throws<IOException>(() => Provider("https://different.invalid"));
    }

    [Theory]
    [InlineData(CommandType.SetBackendUrl)]
    [InlineData(CommandType.SetStandaloneMode)]
    public async Task DirectDispatchRejectsForgedIdentityBeforeAnyDependenciesOrEffects(CommandType command)
    {
        var raw = $$"""{"Command":{{(int)command}},"BackendDns":"attacker.invalid","Username":"SYSTEM","Privileged":true,"UserSid":"S-1-5-18"}""";
        var cmd = JsonSerializer.Deserialize(raw, ValeniusJsonContext.Default.PipeCommand)!;
        var server = new PipeServer(null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<PipeServer>.Instance);
        var result = await server.ProcessAsync(cmd, "SYSTEM", "S-1-5-18", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("administrator", result.Error!);
    }

    [Fact]
    public async Task RealNamedPipeIdentifiesRestrictedCallerWithoutTrustingServerIdentity()
    {
        using var current = WindowsIdentity.GetCurrent();
        if (current.IsSystem) return; // SYSTEM is intentionally privileged even with disabled groups.
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var bytes = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(bytes, 0);
        var sid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sid, bytes.Length);
            var disabled = new[] { new SidAndAttributes { Sid = sid } };
            Assert.True(CreateRestrictedToken(current.AccessToken, 1, 1, disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out var token),
                "CreateRestrictedToken failed: " + Marshal.GetLastWin32Error());
            using (token)
            {
                var name = "Valenius-provisioning-test-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var connect = Task.Run(() => WindowsIdentity.RunImpersonated(token, () =>
                {
                    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
                    client.Connect(5000);
                    client.WriteByte(1);
                    client.Flush();
                    client.ReadByte();
                }));
                await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await server.ReadExactlyAsync(new byte[1]);
                var caller = PipeServer.ReadCallerIdentity(server);
                Assert.Equal(current.User!.Value, caller.Sid);
                Assert.False(caller.Privileged);
                server.WriteByte(1);
                await connect.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
        uint disabledCount, SidAndAttributes[] disabled, uint deletedCount, IntPtr deleted,
        uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle token);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
