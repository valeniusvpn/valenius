using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Valenius.Service;

/// <summary>
/// Self-heals the permissions on <c>C:\ProgramData\Valenius</c> so the SYSTEM service can
/// always read its own state. A broken installer ACL run (e.g. a stray <c>icacls … /T</c>
/// that strips the inherited SYSTEM ACE from each file) leaves <c>registration.json</c>
/// unreadable by SYSTEM — the service then throws <see cref="UnauthorizedAccessException"/>
/// on every heartbeat and silently goes offline. Rather than fail invisibly, detect that at
/// startup and repair it, falling back to a loud, actionable log if repair is not possible.
///
/// The DACL-only repair works because the installer sets SYSTEM as the OWNER of the tree, and
/// the owner retains READ_CONTROL + WRITE_DAC even when the DACL itself denies SYSTEM — so the
/// service can rewrite the DACL. It re-establishes SYSTEM + Administrators FullControl on the
/// folder (kept protected, so children never inherit <c>Users:(RX)</c> from
/// <c>C:\ProgramData</c>) and re-enables inheritance on every child so each picks up that DACL.
/// It never sets a protected DACL on a child (that is the trap that caused the breakage in the
/// first place).
///
/// That assumption breaks down when an individual FILE's OWNER (not just its DACL) drifts away
/// from SYSTEM — e.g. an AV product quarantining and restoring a file, a backup/sync agent, or a
/// leftover from a client version that predates this ACL discipline. SYSTEM then has no implicit
/// rights on that one file even though it still owns the rest of the tree, and the DACL-only
/// reset throws on it specifically. <see cref="EnsureAccessible"/>'s optional ownership-takeover
/// escalation (opt-in per call — see its <c>allowOwnershipTakeover</c> parameter) closes that gap
/// by forcibly reclaiming ownership via <c>takeown.exe</c> (SYSTEM holds SeTakeOwnershipPrivilege,
/// so this always succeeds regardless of the current owner/DACL), then re-running the DACL reset.
/// </summary>
internal static class DataDirSelfHeal
{
    public enum Result
    {
        /// <summary>Nothing needed fixing.</summary>
        Healthy,
        /// <summary>Was broken; the DACL-only reset fixed it. Not alert-worthy — this is the
        /// ordinary, expected repair path.</summary>
        Repaired,
        /// <summary>Was broken; the DACL-only reset alone was not enough — fixed only after
        /// forcibly reclaiming ownership. Worth flagging upstream: an object under this tree lost
        /// its SYSTEM ownership somehow, which is unusual on a folder only SYSTEM should ever
        /// write to.</summary>
        RepairedViaOwnershipTakeover,
        /// <summary>Still broken after every attempt.</summary>
        Failed,
    }

    /// <summary>Runs the ACL self-heal if needed.</summary>
    /// <param name="allowOwnershipTakeover">
    /// When false (the automatic startup path), only the DACL-only reset is attempted — safe and
    /// non-invasive, but can't fix a per-file ownership drift. When true (currently only the
    /// admin-triggered on-demand "Repair client config" action), a DACL-only failure escalates to
    /// forcibly reclaiming ownership via takeown.exe before retrying. Scoped strictly to
    /// <paramref name="dataDir"/>; SYSTEM already has unrestricted authority over the local
    /// machine, so reclaiming ownership of its own exclusive-use data folder carries no
    /// meaningful additional risk beyond what SYSTEM can already do.
    /// </param>
    public static Result EnsureAccessible(string dataDir, ILogger logger, bool allowOwnershipTakeover = false)
    {
        bool needHeal;
        try
        {
            Directory.CreateDirectory(dataDir);
            // Read OR write denial both count: the service reads registration.json (identity) but
            // also WRITES registration.json / autoconnect.json every heartbeat. A file that is
            // readable-but-not-writable lets the client run yet spams a persist failure each cycle,
            // so probe write on the state files too — not just read. Also treat any ReadOnly
            // attribute as needing heal: nothing under this SYSTEM-only tree should ever be
            // read-only, a read-only file still passes the plain read-access probe above (only
            // writes are blocked), and AV/security software is known to leave files here read-only
            // (see StateFileWriter) — left unchecked it would silently defeat this whole self-heal
            // for any file that isn't one of the two hardcoded state files.
            needHeal = HasInaccessibleFile(dataDir) || HasUnwritableStateFile(dataDir) || HasReadOnlyFile(dataDir);
        }
        catch (UnauthorizedAccessException)
        {
            needHeal = true; // can't even enumerate the folder
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Data-dir accessibility check failed (non-permission); skipping self-heal.");
            return Result.Healthy; // not a permission problem — nothing to report upstream
        }

        if (!needHeal)
            return Result.Healthy;

        if (!OperatingSystem.IsWindows())
            return Result.Healthy;

        logger.LogWarning("Data directory {Dir} is not fully accessible to the SYSTEM service; attempting ACL self-heal.", dataDir);
        try
        {
            RepairDaclOnly(dataDir, logger);
            if (HasInaccessibleFile(dataDir) || HasUnwritableStateFile(dataDir) || HasReadOnlyFile(dataDir))
                throw new UnauthorizedAccessException("state files still not fully accessible after DACL-only repair");
            logger.LogInformation("Data directory ACL self-heal succeeded for {Dir}.", dataDir);
            return Result.Repaired;
        }
        catch (Exception ex)
        {
            // Reachable when SYSTEM is not the owner of one or more objects in the tree (e.g. AV
            // quarantine/restore, a backup agent, or a legacy poisoned file), so the DACL-only
            // reset can't rewrite their DACL.
            if (!allowOwnershipTakeover)
            {
                LogAndRecordFailure(dataDir, logger, ex);
                return Result.Failed;
            }

            logger.LogWarning(ex,
                "DACL-only self-heal failed for {Dir}; escalating to forcibly reclaiming ownership.", dataDir);
            try
            {
                RunTakeown(dataDir, logger);
                RepairDaclOnly(dataDir, logger);
                if (HasInaccessibleFile(dataDir) || HasUnwritableStateFile(dataDir) || HasReadOnlyFile(dataDir))
                    throw new UnauthorizedAccessException("state files still not fully accessible after ownership takeover");
                logger.LogWarning(
                    "Data directory ACL self-heal for {Dir} required forcibly reclaiming ownership — now repaired.",
                    dataDir);
                return Result.RepairedViaOwnershipTakeover;
            }
            catch (Exception ex2)
            {
                LogAndRecordFailure(dataDir, logger, ex2);
                return Result.Failed;
            }
        }
    }

    /// <summary>Human-readable detail (repair command included) from the most recent failed repair
    /// attempt — set only when <see cref="EnsureAccessible"/> returns <see cref="Result.Failed"/>.
    /// Read by the caller to report the failure upstream (Admin/Alerts) without re-deriving the
    /// message.</summary>
    public static string? FailureDetail { get; private set; }

    private static void LogAndRecordFailure(string dataDir, ILogger logger, Exception ex)
    {
        FailureDetail = $"ACL self-heal failed for {dataDir}: {ex.Message}. Repair from an elevated " +
            "prompt (takeown seizes ownership first), then restart the Valenius service: " +
            $"takeown /f \"{dataDir}\" /r /d y && icacls \"{dataDir}\" /reset /T /C && " +
            $"icacls \"{dataDir}\" /grant *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /T /C";
        logger.LogError(ex, "ACL self-heal FAILED for {Dir}. {RepairCommand}", dataDir, FailureDetail);
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

    /// <summary>True if any file under <paramref name="dataDir"/> carries a ReadOnly attribute.
    /// Nothing under this SYSTEM-only tree should ever be read-only — a read-only file still
    /// passes <see cref="HasInaccessibleFile"/>'s plain read-access probe (only writes are
    /// blocked), so without this check the self-heal would never even notice a file stuck this
    /// way unless it happened to be one of the two hardcoded state files.</summary>
    private static bool HasReadOnlyFile(string dataDir)
    {
        foreach (var f in EnumerateFilesSafe(dataDir))
        {
            try
            {
                if (File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly))
                    return true;
            }
            catch
            {
                // Gone / transient — not what we're checking for here.
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
    private static void RepairDaclOnly(string dataDir, ILogger logger)
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
        //    directory is fixed before its contents, so files inherit the corrected DACL. This
        //    step relies on SYSTEM already owning each child (see RunTakeown below for when it
        //    doesn't) — it never calls SetOwner itself.
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
                    // Clear ReadOnly first — nothing under this tree should ever carry it, and
                    // AV/security software is known to leave files here read-only (see
                    // StateFileWriter), which File.Open/File.Delete reject with the identical
                    // "Access is denied" exception as a genuine ACL denial.
                    var attr = File.GetAttributes(path);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);

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

    /// <summary>
    /// Forcibly reclaims ownership of every object under <paramref name="dataDir"/> for SYSTEM
    /// (the identity this service always runs as), via <c>takeown.exe /r</c>. SYSTEM holds
    /// SeTakeOwnershipPrivilege, which lets it become the owner of any object regardless of the
    /// object's current owner or DACL — the same mechanism an admin uses manually from an
    /// elevated prompt (see the command in <see cref="LogAndRecordFailure"/>), just run
    /// in-process instead of requiring console access. Best-effort: individual files can still
    /// fail (e.g. held open by another process); the caller re-checks accessibility afterward
    /// rather than trusting the exit code alone. No `/a` switch — omitting it assigns ownership to
    /// the current identity (SYSTEM, since that's who's running this process), matching the SYSTEM
    /// SID the installer itself uses (`icacls /setowner *S-1-5-18`).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RunTakeown(string dataDir, ILogger logger)
    {
        var psi = new ProcessStartInfo("takeown.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("/f");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("/r");
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("y");

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
        {
            logger.LogWarning("Could not start takeown.exe for {Dir}.", dataDir);
            return;
        }
        proc.BeginErrorReadLine();
        // Stdout isn't needed; discard it via the same async pattern to avoid pipe-buffer deadlock.
        proc.StandardOutput.ReadToEndAsync();

        if (!proc.WaitForExit(60_000))
        {
            logger.LogWarning("takeown.exe timed out for {Dir}; killing it.", dataDir);
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return;
        }

        if (proc.ExitCode != 0)
            logger.LogWarning("takeown.exe exited {Code} for {Dir}: {Stderr}", proc.ExitCode, dataDir, stderr.ToString().Trim());
        else
            logger.LogInformation("takeown.exe reclaimed ownership for {Dir}.", dataDir);
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
