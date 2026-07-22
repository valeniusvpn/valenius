namespace Valenius.Service;

/// <summary>
/// Writes the service's small JSON state files (registration.json, autoconnect.json) and other
/// SYSTEM-owned files under the data directory (e.g. the plaintext temp .conf staged for
/// wireguard.exe), retrying briefly on transient <see cref="UnauthorizedAccessException"/> /
/// <see cref="IOException"/> and clearing a ReadOnly attribute if the target already exists.
///
/// Two distinct causes produce the identical "Access to the path ... is denied"
/// UnauthorizedAccessException here, and both are handled:
/// 1. Transient: a real-time AV scanner (e.g. Bitdefender) opens the file to scan it immediately
///    after a write, so the service's next write momentarily collides with the scanner's handle.
///    The retry-with-backoff loop below covers this — the scanner releases its handle within a
///    few ms, so a short wait and retry succeeds without any special-casing.
/// 2. Persistent: the same AV/security software (or another process) leaves the file marked
///    ReadOnly — File.Open/File.Delete throw the exact same exception/message for a read-only
///    file as for a genuine ACL denial, and a read-only attribute does NOT clear itself on retry,
///    so without explicitly clearing it every attempt would fail identically. Cleared once before
///    the loop starts.
///
/// This is NOT permission/ACL handling — permissions live on C:\ProgramData\Valenius (set
/// once by install-service.ps1) and are inherited; see DataDirSelfHeal for that. If the error is
/// genuinely persistent beyond both of the above (e.g. a real ACL/ownership deny), the final
/// attempt still throws so the real problem is not masked.
/// </summary>
internal static class StateFileWriter
{
    private const int MaxAttempts = 5;

    public static void WriteAllText(string path, string contents) =>
        Write(path, () => File.WriteAllText(path, contents));

    public static void WriteAllBytes(string path, byte[] bytes) =>
        Write(path, () => File.WriteAllBytes(path, bytes));

    private static void Write(string path, Action write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearReadOnlyIfSet(path);
                write();
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       && attempt < MaxAttempts)
            {
                // Back off (50/100/150/200 ms) and retry — the scanner releases its handle
                // within a few ms.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void ClearReadOnlyIfSet(string path)
    {
        if (!File.Exists(path)) return;
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
    }
}
