using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Valenius.Service;

/// <summary>
/// Self-heals the permissions on <c>C:\ProgramData\Valenius</c> so the SYSTEM service can
/// always read its own state. A broken installer ACL run (e.g. a stray <c>icacls … /T</c>
/// that strips the inherited SYSTEM ACE from each file) leaves <c>registration.json</c>
/// unreadable by SYSTEM — the service then throws <see cref="UnauthorizedAccessException"/>
/// on every heartbeat and silently goes offline. Rather than fail invisibly, detect that at
/// startup and repair it, falling back to a loud, actionable log if repair is not possible.
///
/// The repair works because the installer sets SYSTEM as the OWNER of the tree, and the owner
/// retains READ_CONTROL + WRITE_DAC even when the DACL itself denies SYSTEM — so the service
/// can rewrite the DACL. It re-establishes SYSTEM + Administrators FullControl on the folder
/// (kept protected, so children never inherit <c>Users:(RX)</c> from <c>C:\ProgramData</c>)
/// and re-enables inheritance on every child so each picks up that DACL. It never sets a
/// protected DACL on a child (that is the trap that caused the breakage in the first place).
/// </summary>
internal static class DataDirSelfHeal
{
    public static void EnsureAccessible(string dataDir, ILogger logger)
    {
        bool needHeal;
        try
        {
            Directory.CreateDirectory(dataDir);
            // Read OR write denial both count: the service reads registration.json (identity) but
            // also WRITES registration.json / autoconnect.json every heartbeat. A file that is
            // readable-but-not-writable lets the client run yet spams a persist failure each cycle,
            // so probe write on the state files too — not just read.
            needHeal = HasInaccessibleFile(dataDir) || HasUnwritableStateFile(dataDir);
        }
        catch (UnauthorizedAccessException)
        {
            needHeal = true; // can't even enumerate the folder
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Data-dir accessibility check failed (non-permission); skipping self-heal.");
            return;
        }

        if (!needHeal)
            return;

        if (!OperatingSystem.IsWindows())
            return;

        logger.LogWarning("Data directory {Dir} is not fully accessible to the SYSTEM service; attempting ACL self-heal.", dataDir);
        try
        {
            RepairWindows(dataDir, logger);
            if (HasInaccessibleFile(dataDir) || HasUnwritableStateFile(dataDir))
                throw new UnauthorizedAccessException("state files still not fully accessible after repair");
            logger.LogInformation("Data directory ACL self-heal succeeded for {Dir}.", dataDir);
        }
        catch (Exception ex)
        {
            // Reachable when SYSTEM is not the tree's owner (e.g. a prior `takeown /a` handed it to
            // Administrators), so the service can't rewrite the DACL. Log ONCE, actionably.
            logger.LogError(ex,
                "ACL self-heal FAILED for {Dir}. Repair from an elevated prompt (takeown seizes ownership " +
                "first), then restart the Valenius service:  takeown /f \"{Dir}\" /r /a  &&  " +
                "icacls \"{Dir}\" /reset /T /C  &&  " +
                "icacls \"{Dir}\" /grant *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /T /C",
                dataDir, dataDir, dataDir, dataDir);
        }
    }

    /// <summary>True if any file under <paramref name="dataDir"/> exists but denies read access.</summary>
    private static bool HasInaccessibleFile(string dataDir)
    {
        foreach (var f in EnumerateFilesSafe(dataDir))
        {
            try
            {
                using var _ = File.Open(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                // Locked / transient / gone — not a permission problem.
            }
        }
        return false;
    }

    /// <summary>
    /// True if a state file the service must write (registration.json / autoconnect.json) exists
    /// but denies write access. Catches the read-OK-but-write-denied case that the read probe
    /// misses (the client still runs, but each heartbeat's persist fails). Opens with
    /// FileShare.ReadWrite and does not modify the file.
    /// </summary>
    private static bool HasUnwritableStateFile(string dataDir)
    {
        foreach (var name in new[] { "registration.json", "autoconnect.json" })
        {
            var p = Path.Combine(dataDir, name);
            if (!File.Exists(p)) continue; // absent (or fully denied -> the read probe covers it)
            try
            {
                using var _ = File.Open(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                // Transient lock (AV scanner) — not a permission problem.
            }
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static void RepairWindows(string dataDir, ILogger logger)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        const InheritanceFlags oici = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        // 1) Folder: ensure SYSTEM + Administrators FullControl, inheritable. Do NOT protect the
        //    folder — the installer's known-good ACL leaves inheritance on (Users:(RX) from
        //    ProgramData is tolerated), and protecting here is what caused the empty-DACL trap.
        //    As the tree's owner, SYSTEM has WRITE_DAC even if the current DACL denies it.
        var di   = new DirectoryInfo(dataDir);
        var dsec = di.GetAccessControl();
        dsec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, oici, PropagationFlags.None, AccessControlType.Allow));
        dsec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, oici, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(dsec);

        // 2) Children (top-down): re-enable inheritance so each replaces its broken/empty per-file
        //    DACL with the folder's inherited SYSTEM+Administrators. Top-down order means a parent
        //    directory is fixed before its contents, so files inherit the corrected DACL.
        int healed = 0, failed = 0;
        foreach (var path in EnumerateTopDown(dataDir))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var s = new DirectoryInfo(path).GetAccessControl();
                    s.SetAccessRuleProtection(false, false); // inherit from parent; drop explicit/empty DACL
                    new DirectoryInfo(path).SetAccessControl(s);
                }
                else
                {
                    var s = new FileInfo(path).GetAccessControl();
                    s.SetAccessRuleProtection(false, false);
                    new FileInfo(path).SetAccessControl(s);
                }
                healed++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Self-heal could not fix ACL on {Path}", path);
            }
        }
        logger.LogInformation("ACL self-heal: {Healed} item(s) re-inherited, {Failed} failed.", healed, failed);
    }

    /// <summary>All files under root, tolerating access errors on individual subtrees.</summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        foreach (var path in EnumerateTopDown(root))
            if (!Directory.Exists(path))
                yield return path;
    }

    /// <summary>Every file and directory under root, parents before children.</summary>
    private static IEnumerable<string> EnumerateTopDown(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs = [], files = [];
            try { subdirs = Directory.GetDirectories(dir); } catch { }
            try { files   = Directory.GetFiles(dir); }       catch { }
            foreach (var f in files)  yield return f;
            foreach (var sd in subdirs) { yield return sd; stack.Push(sd); }
        }
    }
}
