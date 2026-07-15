namespace Valenius.Service;

/// <summary>
/// Writes the service's small JSON state files (registration.json, autoconnect.json),
/// retrying briefly on transient <see cref="UnauthorizedAccessException"/> /
/// <see cref="IOException"/>.
///
/// These transient failures occur when a real-time AV scanner (e.g. Bitdefender) opens the
/// file to scan it immediately after a write, so the service's next write momentarily
/// collides with the scanner's handle and is denied. The ACL is correct and the data does
/// persist on the successful attempts; this just smooths over the scan race so it does not
/// surface as a spurious "could not persist registration state" error every few minutes.
///
/// This is NOT permission/ACL handling — permissions live on C:\ProgramData\Valenius (set
/// once by install-service.ps1) and are inherited. If the error is genuinely persistent
/// (e.g. a real deny), the final attempt still throws so the real problem is not masked.
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
}
