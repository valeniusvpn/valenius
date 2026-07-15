#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or reinstalls) the Valenius Windows service.
.PARAMETER InstallDir
    Root installation directory, e.g. C:\Program Files\Valenius
.PARAMETER SetupDir
    Directory the installer EXE was run from.  Used to locate valenius-setup.ini.
.PARAMETER BackendUrl
    Backend URL to write into appsettings.json (overrides valenius-setup.ini).
    Passed via /BACKENDURL= on the installer command line.
.PARAMETER ApiKey
    API key to write into appsettings.json (overrides valenius-setup.ini).
    Passed via /APIKEY= on the installer command line.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallDir,
    [string] $SetupDir   = "",
    [string] $BackendUrl = "",
    [string] $ApiKey     = ""
)

$ErrorActionPreference = 'Stop'

# --- Fix data directory permissions ------------------------------------------
# Grant SYSTEM + Administrators FullControl on the whole tree. This is the known-good,
# fleet-proven sequence; it repairs the empty-DACL trap older builds could leave behind
# (registration.json / autoconnect.json owned by a non-SYSTEM principal with an empty DACL
# the service could neither read, write, nor re-ACL):
#   1. /setowner *S-1-5-18 /T -- seize ownership of every file (privilege-based; succeeds
#                                regardless of the current owner or an empty DACL).
#   2. /reset /T              -- drop every per-file explicit/empty DACL so children inherit.
#   3. /grant ... /T          -- add an EXPLICIT SYSTEM + Administrators FullControl ACE to the
#                                folder AND every child, so SYSTEM always retains access even on
#                                a file whose inheritance was somehow broken.
# Idempotent; safe on every install/update. *S-1-5-18 = NT AUTHORITY\SYSTEM,
# *S-1-5-32-544 = BUILTIN\Administrators.
#
# Do NOT add /inheritance:r here. Blocking inheritance turns each child into a protected
# ACL, and (OI)(CI) grant flags don't apply to a leaf FILE, so /inheritance:r strips the
# file's SYSTEM ACE without re-adding it -- the SYSTEM service then cannot read its own
# registration.json and silently goes offline (regression fixed by reverting to this form).
# The trade-off: BUILTIN\Users:(RX) inherited from C:\ProgramData remains readable. That
# hardening is deferred until it can ship without stranding the fleet; DataDirSelfHeal in
# the service is the runtime safety net if a data-dir ACL ever ends up unreadable again.
$DataDir = Join-Path $env:ProgramData 'Valenius'
# Create the directory up front so its ACL is correct BEFORE the service (SYSTEM) starts
# and writes registration.json into it. Done unconditionally, not "if it exists" -- a fresh
# install has no directory yet, and an upgrade may have a stale one to repair.
if (-not (Test-Path $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    Write-Host "Created data directory: $DataDir"
}
& icacls.exe $DataDir /setowner "*S-1-5-18" /T /C 2>$null | Out-Null
& icacls.exe $DataDir /reset /T /C 2>$null | Out-Null
& icacls.exe $DataDir /grant "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /T /C 2>$null | Out-Null
Write-Host "Data directory permissions ensured (SYSTEM + Administrators FullControl): $DataDir"

# --- Write appsettings.json --------------------------------------------------
# Priority order:
#   1. -BackendUrl / -ApiKey command-line params  (MDM / scripted installs)
#   2. valenius-setup.ini in the same folder as the installer  (download package)
#   3. Existing appsettings.json at the install location  (update - preserve)
#   4. Warning if none found (service will not connect)

$AppSettingsPath = Join-Path $InstallDir 'service\appsettings.json'
$resolvedUrl     = $BackendUrl.Trim()
$resolvedKey     = $ApiKey.Trim()

if ((-not $resolvedUrl) -and (-not $resolvedKey) -and $SetupDir) {
    $iniPath = Join-Path $SetupDir 'valenius-setup.ini'
    if (Test-Path $iniPath) {
        foreach ($line in (Get-Content $iniPath)) {
            if ($line -match '^\s*BackendUrl\s*=\s*(.+)$') { $resolvedUrl = $Matches[1].Trim() }
            if ($line -match '^\s*ApiKey\s*=\s*(.+)$')     { $resolvedKey = $Matches[1].Trim() }
        }
        Write-Host "appsettings.json: read from valenius-setup.ini"
    }
}

# Clients talk to the backend over TLS only (registration, health probe and mTLS all
# assume https). Coerce a stray http:// to https:// and drop a trailing slash. Without
# this, an http:// URL (e.g. from a package .ini built over http, or a /BACKENDURL=http://
# param) makes the reverse proxy 301 the POST; HttpClient follows it as a GET and the
# backend returns 405 on /api/clients/register. A trailing slash can produce a //api path.
if ($resolvedUrl) {
    $resolvedUrl = $resolvedUrl.TrimEnd('/')
    if ($resolvedUrl -match '^http://') {
        $resolvedUrl = 'https://' + $resolvedUrl.Substring(7)
        Write-Host "appsettings.json: upgraded BackendUrl scheme to https"
    }
}

if ($resolvedUrl -and $resolvedKey) {
    # Use ConvertTo-Json so special characters in URL / key are safely escaped.
    $cfg = [ordered]@{
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default                     = 'Information'
                'Microsoft.Hosting.Lifetime' = 'Information'
            }
        }
        Valenius = [ordered]@{
            BackendUrl = $resolvedUrl
            ApiKey     = $resolvedKey
        }
    }
    $cfg | ConvertTo-Json -Depth 5 | Set-Content -Path $AppSettingsPath -Encoding UTF8
    Write-Host "appsettings.json: written (BackendUrl=$resolvedUrl)"
} elseif (Test-Path $AppSettingsPath) {
    # Update with no new config: keep the existing appsettings.json, but self-heal a stray
    # http:// BackendUrl (and trailing slash) so clients that shipped with http stop 405ing
    # on the next auto-update without any manual edit.
    try {
        $existing = Get-Content $AppSettingsPath -Raw | ConvertFrom-Json
        $cur = "$($existing.Valenius.BackendUrl)".Trim().TrimEnd('/')
        if ($cur -match '^http://') {
            $existing.Valenius.BackendUrl = 'https://' + $cur.Substring(7)
            $existing | ConvertTo-Json -Depth 5 | Set-Content -Path $AppSettingsPath -Encoding UTF8
            Write-Host "appsettings.json: preserved; upgraded BackendUrl scheme to https"
        } elseif ($cur -ne "$($existing.Valenius.BackendUrl)") {
            $existing.Valenius.BackendUrl = $cur
            $existing | ConvertTo-Json -Depth 5 | Set-Content -Path $AppSettingsPath -Encoding UTF8
            Write-Host "appsettings.json: preserved; trimmed trailing slash from BackendUrl"
        } else {
            Write-Host "appsettings.json: preserved (update - no new config supplied)"
        }
    } catch {
        Write-Host "appsettings.json: preserved (update - could not parse to normalize scheme)"
    }
} else {
    # Fresh install: the installer copies a default appsettings.json only when the
    # file does not already exist (Check: condition in Valenius.iss).
    # If it is still missing here, file extraction failed or no default was bundled.
    Write-Warning "appsettings.json: not found - service may not start."
    Write-Warning "Re-run installer with /BACKENDURL= and /APIKEY=, or use valenius-setup.ini."
}

$ServiceName = 'Valenius'
$DisplayName = 'Valenius Service'
$Description = 'Manages WireGuard VPN tunnels for non-administrative users.'
$BinaryPath  = Join-Path $InstallDir 'service\Valenius.Service.exe'

if (-not (Test-Path $BinaryPath)) {
    Write-Error "Service binary not found: $BinaryPath"
    exit 1
}

# --- Stop and remove any existing installation -------------------------------

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing) {
    Write-Host "Stopping existing $ServiceName service..."
    try {
        if ($existing.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
            }
        }
    }
    catch {
        Write-Warning "Could not stop service gracefully: $_"
    }

    Write-Host "Removing existing $ServiceName service..."
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sc.exe delete returned $LASTEXITCODE - proceeding anyway."
    }
    Start-Sleep -Seconds 1
}

# --- Create and start the new service ----------------------------------------
# Errors here are non-fatal: the tray relaunch below must always be attempted.

try {
    Write-Host "Creating $ServiceName service..."

    $params = @{
        Name           = $ServiceName
        BinaryPathName = $BinaryPath
        DisplayName    = $DisplayName
        Description    = $Description
        StartupType    = 'Automatic'
        ErrorAction    = 'Stop'
    }
    New-Service @params

    Write-Host "Starting $ServiceName service..."
    Start-Service -Name $ServiceName -ErrorAction Stop
    Write-Host "$ServiceName service started successfully."
}
catch {
    Write-Warning "Service create/start error (will still relaunch tray): $_"
}

# --- Relaunch tray app in the logged-in user session -------------------------
# The installer runs as SYSTEM (Session 0). The reliable way to launch a GUI
# process in the user's desktop session is WTSQueryUserToken + CreateProcessAsUser,
# which directly targets the active console session without going through Task
# Scheduler (which has race conditions between Start-ScheduledTask and task deletion).

$TrayExe = Join-Path $InstallDir 'trayapp\Valenius.TrayApp.exe'
$LogDir  = Join-Path $env:ProgramData 'Valenius'
$LogFile = Join-Path $LogDir 'update-tray.log'

# Ensure the log directory exists before any Write-Log call.
New-Item -ItemType Directory -Force -Path $LogDir -ErrorAction SilentlyContinue | Out-Null

function Write-Log {
    param([string]$Msg)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Msg"
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
}

Write-Log "Tray relaunch starting. TrayExe: $TrayExe"

# Kill any running instance first (mutex guarantees only one will start).
& taskkill.exe /f /im 'Valenius.TrayApp.exe' 2>$null | Out-Null

$launched = $false

# --- Primary: WTS CreateProcessAsUser ----------------------------------------
# Uses WTSGetActiveConsoleSessionId (simpler than WTSEnumerateSessions -- no
# struct marshaling that can mis-align on x64) + CreateProcessAsUser.
# Requires SeTcbPrivilege, held by the SYSTEM account.
try {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class WtsLauncher {
    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr lpEnv, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    static extern bool DestroyEnvironmentBlock(IntPtr lpEnv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int    cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int    dwX, dwY, dwXSize, dwYSize;
        public int    dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short  wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION {
        public IntPtr hProcess, hThread;
        public int    dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(
        IntPtr hToken, string lpApp, string lpCmd,
        IntPtr lpPA, IntPtr lpTA, bool bInherit, uint dwFlags,
        IntPtr lpEnv, string lpDir,
        ref STARTUPINFO lpSI, out PROCESS_INFORMATION lpPI);

    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint NO_SESSION = 0xFFFFFFFF;

    public static bool Launch(string exePath) {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NO_SESSION) return false;   // no interactive user logged in

        IntPtr token;
        if (!WTSQueryUserToken(sessionId, out token)) {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, "WTSQueryUserToken failed (session=" + sessionId + " err=" + err + ")");
        }

        IntPtr env = IntPtr.Zero;
        try {
            CreateEnvironmentBlock(out env, token, false);
            uint flags = (env != IntPtr.Zero) ? CREATE_UNICODE_ENVIRONMENT : 0u;

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            PROCESS_INFORMATION pi;
            bool ok = CreateProcessAsUser(token, null, "\"" + exePath + "\"",
                IntPtr.Zero, IntPtr.Zero, false, flags, env, null, ref si, out pi);

            if (ok) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
            else {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, "CreateProcessAsUser failed (err=" + err + ")");
            }
            return ok;
        } finally {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(token);
        }
    }
}
'@
    $launched = [WtsLauncher]::Launch($TrayExe)
    if ($launched) {
        Write-Log "WTS launch succeeded."
    } else {
        Write-Log "WTS launch returned false (no active console session) - falling back to scheduled task."
    }
} catch {
    Write-Log "WTS launch exception: $_ - falling back to scheduled task."
}

# --- Fallback: scheduled task ------------------------------------------------
# Used if the WTS method is unavailable (e.g. Add-Type compilation failed).
if (-not $launched) {
    $TaskName = 'ValeniusLaunchTray'
    try {
        $explorerProc = Get-WmiObject Win32_Process -Filter "Name='explorer.exe'" |
            Select-Object -First 1
        if (-not $explorerProc) {
            Write-Log "No explorer.exe found - cannot relaunch tray app (no user logged in)."
        } else {
            $ownerResult = $explorerProc.GetOwner()
            $domain = $ownerResult.Domain
            $user   = $ownerResult.User
            if (-not $user) {
                Write-Log "Could not determine explorer.exe owner."
            } else {
                $logonUser = if ($domain) { "$domain\$user" } else { $user }
                Write-Log "Scheduled task fallback as $logonUser ..."

                & schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null
                $action    = New-ScheduledTaskAction -Execute $TrayExe
                $principal = New-ScheduledTaskPrincipal -UserId $logonUser -LogonType Interactive -RunLevel Limited
                $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 0)
                Register-ScheduledTask -TaskName $TaskName -Action $action `
                    -Principal $principal -Settings $settings -Force | Out-Null
                Start-ScheduledTask -TaskName $TaskName

                $deadline = (Get-Date).AddSeconds(15)
                while ((Get-Date) -lt $deadline) {
                    if (Get-Process 'Valenius.TrayApp' -ErrorAction SilentlyContinue) {
                        $launched = $true
                        break
                    }
                    Start-Sleep -Milliseconds 500
                }
                & schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null
                Write-Log "Scheduled task fallback done. Launched: $launched"
            }
        }
    } catch {
        Write-Log "Scheduled task fallback failed: $_"
    }
}
