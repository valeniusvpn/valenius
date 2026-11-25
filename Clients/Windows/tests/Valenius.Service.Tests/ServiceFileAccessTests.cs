using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Valenius.Service;
using Xunit;

namespace Valenius.Service.Tests;

public sealed class ServiceFileAccessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Valenius-file-access-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_directory, "apikey.dat");

    private ApiKeyProvider Provider() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Valenius:ApiKey"] = "legacy" }).Build(),
        NullLogger<ApiKeyProvider>.Instance, KeyPath);

    private void BroadParent()
    {
        Directory.CreateDirectory(_directory);
        var info = new DirectoryInfo(_directory);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static void AssertPrivate(string path)
    {
        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var allowed = new[] { "S-1-5-18", "S-1-5-32-544", identity.User!.Value };
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Contains(rule.IdentityReference.Value, allowed);
            Assert.False(rule.IsInherited);
        });
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-18" && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, rule => rule.IdentityReference.Value == "S-1-5-32-544" && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    [Fact]
    public void Credential_is_private_on_initial_save_rotation_and_restart()
    {
        BroadParent();
        var provider = Provider();
        provider.Update("vlnd_first-synthetic-key");
        AssertPrivate(KeyPath);
        Assert.Equal("vlnd_first-synthetic-key", Provider().Current);
        provider.Update("vlnd_rotated-synthetic-key");
        AssertPrivate(KeyPath);
        Assert.Equal("vlnd_rotated-synthetic-key", Provider().Current);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Startup_repairs_old_broad_credential_without_changing_its_value()
    {
        BroadParent();
        File.WriteAllText(KeyPath, "vlnd_existing-synthetic-key");
        Assert.False(new FileInfo(KeyPath).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal("vlnd_existing-synthetic-key", Provider().Current);
        AssertPrivate(KeyPath);
    }

    [Fact]
    public void Existing_empty_or_unreadable_credential_cannot_fall_back_to_legacy_key()
    {
        BroadParent();
        File.WriteAllText(KeyPath, "");
        Assert.Throws<IOException>(() => Provider());
        File.Delete(KeyPath);
        Directory.CreateDirectory(KeyPath);
        Assert.ThrowsAny<Exception>(() => Provider());
    }

    [Fact]
    public void Atomic_cache_replacement_preserves_existing_restricted_DACL_and_identity()
    {
        BroadParent();
        var path = Path.Combine(_directory, "registration.json");
        StateFileWriter.WriteAllTextAtomic(path, "original-identity");
        new FileInfo(path).SetAccessControl(ServiceFileAccess.PrivateSecurity());
        var before = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        StateFileWriter.WriteAllTextAtomic(path, "same-identity-new-policy");
        Assert.Equal("same-identity-new-policy", File.ReadAllText(path));
        Assert.Equal(before, new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Repair_does_not_reenable_Users_access_to_credential()
    {
        BroadParent();
        Provider().Update("vlnd_existing-synthetic-key");
        var interrupted = Path.Combine(_directory, "apikey.dat." + Guid.NewGuid().ToString("N") + ".tmp");
        ServiceFileAccess.WriteCredential(interrupted, "vlnd_interrupted-synthetic-key");
        File.SetAttributes(KeyPath, FileAttributes.ReadOnly);
        File.SetAttributes(interrupted, FileAttributes.ReadOnly);
        var other = Path.Combine(_directory, "readonly.json");
        File.WriteAllText(other, "state");
        File.SetAttributes(other, FileAttributes.ReadOnly);
        Assert.NotEqual(DataDirSelfHeal.Result.Failed, DataDirSelfHeal.EnsureAccessible(_directory, NullLogger.Instance));
        AssertPrivate(KeyPath);
        AssertPrivate(interrupted);
        Assert.False(File.GetAttributes(KeyPath).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.GetAttributes(interrupted).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("vlnd_existing-synthetic-key", Provider().Current);
    }

    [Fact]
    public void Blocked_atomic_replace_keeps_previous_cache_and_retry_after_unlock_succeeds()
    {
        BroadParent();
        var path = Path.Combine(_directory, "registration.json");
        StateFileWriter.WriteAllTextAtomic(path, "retained-identity-and-policy");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => StateFileWriter.WriteAllTextAtomic(path, "new-policy"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal("retained-identity-and-policy", File.ReadAllText(path));
        }
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        StateFileWriter.WriteAllTextAtomic(path, "new-policy");
        Assert.Equal("new-policy", File.ReadAllText(path));
    }

    [Fact]
    public async Task Concurrent_cache_writers_keep_complete_files_and_leave_no_temporary_files()
    {
        BroadParent();
        var path = Path.Combine(_directory, "registration.json");
        StateFileWriter.WriteAllTextAtomic(path, new string('a', 4096));
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
            StateFileWriter.WriteAllTextAtomic(path, new string((char)('a' + i % 20), 4096)))));
        var saved = File.ReadAllText(path);
        Assert.Equal(4096, saved.Length);
        Assert.All(saved, c => Assert.Equal(saved[0], c));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Installer_credential_function_constructs_exact_SYSTEM_and_Admin_file_ACEs()
    {
        BroadParent();
        File.WriteAllText(KeyPath, "synthetic-device-key");
        new FileInfo(KeyPath).SetAccessControl(ServiceFileAccess.PrivateSecurity());
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "installer", "scripts", "install-service.ps1")))
            root = root.Parent;
        Assert.NotNull(root);
        var command = """
            $ErrorActionPreference = 'Stop'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($env:VALENIUS_TEST_INSTALLER,[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'Installer syntax error.' }
            $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Protect-ValeniusCredential'},$true)
            if (!$function) { throw 'Installer credential protection function missing.' }
            . ([ScriptBlock]::Create($function.Extent.Text))
            Protect-ValeniusCredential $env:VALENIUS_TEST_KEY
            $acl=Get-Acl -LiteralPath $env:VALENIUS_TEST_KEY
            if (!$acl.AreAccessRulesProtected) { throw 'Credential inheritance remains enabled.' }
            $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
            if ($rules.Count -ne 2) { throw 'Unexpected credential permissions.' }
            foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
                if (!($rules | Where-Object { $_.IdentityReference.Value -eq $sid -and $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights -eq 'FullControl' })) {
                    throw 'Required credential ACE missing.'
                }
            }
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(arg);
        start.Environment["VALENIUS_TEST_INSTALLER"] = Path.Combine(root!.FullName, "installer", "scripts", "install-service.ps1");
        start.Environment["VALENIUS_TEST_KEY"] = KeyPath;
        // A test launched from pwsh must not inject PowerShell 7-only modules into
        // the Windows PowerShell 5.1 process used by the real installer.
        start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "Modules");
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally
        {
            // Synthetic file remains owned by this fixture principal, which retains
            // WRITE_DAC even after the installer removes its data access.
            var restore = new FileSecurity();
            restore.SetSecurityDescriptorSddlForm(ServiceFileAccess.PrivateSecurity()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access);
            new FileInfo(KeyPath).SetAccessControl(restore);
        }
        Assert.Equal("synthetic-device-key", File.ReadAllText(KeyPath));
    }

    [Fact]
    public void Restricted_pipe_caller_cannot_read_key_but_service_writes_and_repairs_use_process_identity()
    {
        BroadParent();
        Provider().Update("vlnd_synthetic-key");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) return; // The production principal is intentionally authorized.
        var ids = new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        var allocated = ids.Select(sid =>
        {
            var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
            var ptr = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }).ToArray();
        try
        {
            Assert.True(CreateRestrictedToken(identity.AccessToken, 1, (uint)allocated.Length,
                allocated.Select(p => new SidAndAttributes { Sid = p }).ToArray(), 0, IntPtr.Zero, 0, IntPtr.Zero, out var token));
            using (token)
                WindowsIdentity.RunImpersonated(token, () =>
                {
                    Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(KeyPath));
                    Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(KeyPath, "attacker"));
                    // The fixture principal models the service process. Caller token must
                    // not determine owners/ACLs or cause a failed atomic cache replacement.
                    ServiceFileAccess.WriteCredential(KeyPath, "vlnd_rotated-synthetic-key");
                    var path = Path.Combine(_directory, "registration.json");
                    StateFileWriter.WriteAllTextAtomic(path, "identity-and-policy");
                    StateFileWriter.WriteAllTextAtomic(path, "updated-policy");
                    Assert.Throws<IOException>(() => ServiceFileAccess.Run(() => throw new IOException("synthetic write failure")));
                    Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(KeyPath));
                });
            Assert.Equal("vlnd_rotated-synthetic-key", Provider().Current);
            AssertPrivate(KeyPath);
        }
        finally { foreach (var ptr in allocated) Marshal.FreeHGlobal(ptr); }
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
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (UnauthorizedAccessException) { } // Parent grants deletion of private synthetic files.
        Directory.Delete(_directory, recursive: true); // Unique synthetic fixture only.
    }
}
