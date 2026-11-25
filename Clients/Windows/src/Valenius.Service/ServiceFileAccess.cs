using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Valenius.Service;

/// <summary>Service-owned storage must never inherit a pipe caller's impersonation token.</summary>
internal static class ServiceFileAccess
{
    internal static bool IsCredentialFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("apikey.dat", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("apikey.dat.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    public static void Run(Action action)
    {
        // All operations in this scope are synchronous. Save the actual thread token,
        // use the service process token for storage, then restore before returning to
        // the caller. Explicit native scoping also works in the trimmed service.
        if (!OpenThreadToken(GetCurrentThread(), 0x000C /* QUERY | IMPERSONATE */, true, out var token))
        {
            var error = Marshal.GetLastWin32Error();
            token?.Dispose();
            if (error != 1008 /* ERROR_NO_TOKEN */) throw new Win32Exception(error);
            action();
            return;
        }
        using (token)
        {
            if (!RevertToSelf()) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { action(); }
            finally
            {
                // Never return to pipe dispatch while accidentally retaining service
                // privileges if Windows cannot restore the original caller token.
                if (!SetThreadToken(IntPtr.Zero, token))
                    Environment.FailFast("Could not restore caller impersonation after service-owned file access.");
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr thread, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadToken(IntPtr thread, SafeAccessTokenHandle token);

    // Production runs as SYSTEM. Including the actual process identity also permits
    // isolated, non-elevated development/tests without granting the Users group access.
    internal static FileSecurity PrivateSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            identity.User!
        }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.SetOwner(identity.User!);
        return security;
    }

    public static void ProtectCredential(string path) => Run(() =>
    {
        RejectReparsePoint(path);
        new FileInfo(path).SetAccessControl(PrivateSecurity());
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    });

    public static void WriteCredential(string path, string contents) => WritePrivateText(path, contents);

    public static void WritePrivateText(string path, string contents) => Run(() =>
    {
        RejectReparsePoint(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Supply a protected DACL at CREATE time, before the first secret byte.
            using (var file = new FileInfo(temporary).Create(FileMode.CreateNew,
                       FileSystemRights.FullControl, FileShare.None, 4096,
                       FileOptions.WriteThrough, PrivateSecurity()))
            {
                using (var writer = new StreamWriter(file, leaveOpen: true))
                {
                    writer.Write(contents);
                    writer.Flush();
                }
                file.Flush(flushToDisk: true);
            }
            StateFileWriter.Retry(path, () =>
            {
                RejectReparsePoint(path);
                // Move intentionally carries the private temporary file's DACL into
                // the destination, replacing any old, overly broad credential DACL.
                File.Move(temporary, path, overwrite: true);
            });
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    });

    internal static void RejectReparsePoint(string path)
    {
        // Do not follow a pre-existing link when repairing or replacing a credential.
        for (var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo;
             current is not null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Service credential path must not contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
