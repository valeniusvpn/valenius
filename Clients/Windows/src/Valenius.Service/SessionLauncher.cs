using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Valenius.Service;

/// <summary>
/// Launches a process in the active interactive user session from a SYSTEM service.
/// Requires SeTcbPrivilege, which is held by the SYSTEM account.
/// </summary>
internal static class SessionLauncher
{
    // ── Win32 imports ─────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int    cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int    dwX, dwY, dwXSize, dwYSize;
        public int    dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short  wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int    dwProcessId, dwThreadId;
    }

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint NO_ACTIVE_SESSION = 0xFFFFFFFF;
    // Session exists but no user is interactively logged in yet (early boot, login screen, cleanup).
    private const int  ERROR_NO_TOKEN = 1008;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches <paramref name="exePath"/> in the first available interactive user session.
    /// Tries the physical console session first, then falls back to any session that owns
    /// an <c>explorer.exe</c> process — this covers RDP-only connections where the console
    /// session has no user token.
    /// Returns false when no user is logged in at all.
    /// </summary>
    public static bool LaunchInUserSession(string exePath)
    {
        // Collect candidate session IDs without struct-marshaling WTSEnumerateSessions:
        //   1. The active console session (user at the physical display).
        //   2. Any session that owns explorer.exe (catches RDP-only logins).
        var candidates = new List<uint>();

        var consoleSession = WTSGetActiveConsoleSessionId();
        if (consoleSession != NO_ACTIVE_SESSION)
            candidates.Add(consoleSession);

        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            var sid = (uint)proc.SessionId;
            if (sid != 0 && !candidates.Contains(sid))
                candidates.Add(sid);
        }

        foreach (var sessionId in candidates)
        {
            if (TryLaunchInSession(exePath, sessionId))
                return true;
        }

        return false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool TryLaunchInSession(string exePath, uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_NO_TOKEN: session exists but no interactive user token yet
            // (login screen, early boot, locked-down session) — try next candidate.
            if (err == ERROR_NO_TOKEN)
                return false;
            throw new Win32Exception(err, "WTSQueryUserToken");
        }

        IntPtr env = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out env, userToken, false);

            var si = new STARTUPINFO
            {
                cb        = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default"
            };

            uint flags = env != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0u;
            bool ok = CreateProcessAsUser(
                userToken, null, $"\"{exePath}\"",
                IntPtr.Zero, IntPtr.Zero, false, flags,
                env, null, ref si, out var pi);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NO_TOKEN)
                    return false;
                throw new Win32Exception(err, "CreateProcessAsUser");
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(userToken);
        }
    }
}
