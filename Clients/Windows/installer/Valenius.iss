#define MyAppName      "Valenius"
#define MyAppVersion   "1.17.0"
#define MyAppPublisher "Stranto Business Solutions GmbH"
#define MyAppURL       "https://valenius.stranto.com"
#define MyServiceExe   "Valenius.Service.exe"
#define MyTrayExe      "Valenius.TrayApp.exe"

[Setup]
; *** IMPORTANT: keep this AppId constant across releases so the installer
;     recognises upgrades.  Generate a new GUID only when forking to a
;     completely different product.
AppId={{609DBF65-A3A3-4BD2-AC66-526A975AA6C3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ValeniusSetup-{#MyAppVersion}
SetupIconFile=logo.ico
WizardImageFile=wizard-banner.png
WizardSmallImageFile=wizard-small.png
ExtraDiskSpaceRequired=167772160
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Code signing is handled by build-installer.ps1 (run with -Sign).
; The inner EXEs are signed before packaging; the installer EXE is signed after.
PrivilegesRequired=admin
; x64 and native ARM64 (e.g. Microsoft Surface)
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
; Don't use Restart Manager - it can't cross the elevation boundary to the
; user-session tray process and logs SID-mismatch errors.  The [Code] section
; kills the tray app with taskkill /f before files are overwritten instead.
CloseApplications=no
RestartApplications=no
; Uninstaller
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\trayapp\{#MyTrayExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ---------------------------------------------------------------------------
; Files
; ---------------------------------------------------------------------------
[Files]
; Windows Service — binaries (always overwritten on update)
Source: "publish\service-x64\*";   DestDir: "{app}\service"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "appsettings*.json"; Check: not IsARM64
Source: "publish\service-arm64\*"; DestDir: "{app}\service"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "appsettings*.json"; Check: IsARM64

; appsettings.json — installed only when the file does not already exist.
; On a fresh install this provides working defaults so the service can start.
; On an update the existing file (which may have a custom BackendUrl/ApiKey)
; is preserved.  install-service.ps1 overwrites it when valenius-setup.ini or
; /BACKENDURL= params are supplied.
Source: "publish\service-x64\appsettings.json";   DestDir: "{app}\service"; \
  Check: (not IsARM64) and (not FileExists(ExpandConstant('{app}\service\appsettings.json')))
Source: "publish\service-arm64\appsettings.json"; DestDir: "{app}\service"; \
  Check: IsARM64 and (not FileExists(ExpandConstant('{app}\service\appsettings.json')))

; TrayApp
Source: "publish\trayapp-x64\*";   DestDir: "{app}\trayapp"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsARM64
Source: "publish\trayapp-arm64\*"; DestDir: "{app}\trayapp"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsARM64

; Bundled WireGuard binaries (WireGuard 1.x - wireguard.exe only, no wintun.dll)
Source: "wireguard\x64\wireguard.exe";   DestDir: "{app}\service"; Flags: ignoreversion; Check: not IsARM64
Source: "wireguard\arm64\wireguard.exe"; DestDir: "{app}\service"; Flags: ignoreversion; Check: IsARM64

; Helper scripts (used by installer and by the auto-update mechanism)
Source: "scripts\install-service.ps1";   DestDir: "{app}"; Flags: ignoreversion
Source: "scripts\uninstall-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

; Splash image — TBitmap.LoadFromFile supports BMP at runtime (not PNG)
Source: "..\..\..\Images\valeniusWireguard.bmp"; DestDir: "{tmp}"; Flags: dontcopy

; ---------------------------------------------------------------------------
; Shortcuts
; ---------------------------------------------------------------------------
[Icons]
; Start-menu shortcut
Name: "{commonprograms}\{#MyAppName}\{#MyAppName}"; \
  Filename: "{app}\trayapp\{#MyTrayExe}"; \
  Comment: "Valenius VPN Manager"

; All-users desktop shortcut (C:\Users\Public\Desktop).
; Owned by SYSTEM - regular users cannot delete it without admin rights.
Name: "{commondesktop}\{#MyAppName}"; \
  Filename: "{app}\trayapp\{#MyTrayExe}"; \
  Comment: "Valenius VPN Manager"

; ---------------------------------------------------------------------------
; Autostart for all users via HKLM Run key
; (shows up in Task Manager Startup tab so users can disable it)
; ---------------------------------------------------------------------------
[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#MyAppName}"; \
  ValueData: """{app}\trayapp\{#MyTrayExe}"""; \
  Flags: uninsdeletevalue

; ---------------------------------------------------------------------------
; Run after installation
; ---------------------------------------------------------------------------
[Run]
; Register + start the Windows service.
; {src} = directory where the setup EXE was run from (for valenius-setup.ini lookup).
; /BACKENDURL= and /APIKEY= are optional command-line parameters for MDM/silent installs.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-service.ps1"" -InstallDir ""{app}"" -SetupDir ""{src}"" -BackendUrl ""{param:BackendUrl|}"" -ApiKey ""{param:ApiKey|}"""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Registering Windows service..."

; Launch the tray app immediately for the current user (optional - user can untick)
Filename: "{app}\trayapp\{#MyTrayExe}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent

; ---------------------------------------------------------------------------
; Run before uninstall
; ---------------------------------------------------------------------------
[UninstallRun]
; Stop + delete the Windows service BEFORE files are removed
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-service.ps1"""; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "StopService"

; ---------------------------------------------------------------------------
; Delete leftover data on uninstall
; ---------------------------------------------------------------------------
[UninstallDelete]
; Remove any downloaded update installers left in the data folder
Type: filesandordirs; Name: "{commonappdata}\Valenius\updates"

; ---------------------------------------------------------------------------
; Pascal/Code section
; ---------------------------------------------------------------------------
[Code]

var
  SplashPage:    TWizardPage;
  LicensePage:   TWizardPage;
  AcceptCheckbox: TCheckBox;

procedure OpenLicenseLink(Sender: TObject);
var
  Dummy: Integer;
begin
  ShellExec('open', 'https://www.valenius.com/clientagb.pdf', '', '', SW_SHOWNORMAL, ewNoWait, Dummy);
end;

procedure AcceptCheckboxClick(Sender: TObject);
begin
  WizardForm.NextButton.Enabled := AcceptCheckbox.Checked;
end;

procedure InitializeWizard;
var
  SplashImage: TBitmapImage;
  DescLabel:   TLabel;
  LinkLabel:   TNewStaticText;
begin
  { --- Page 1: Branding splash --- }
  SplashPage := CreateCustomPage(wpWelcome, '', '');
  ExtractTemporaryFile('valeniusWireguard.bmp');
  SplashImage := TBitmapImage.Create(SplashPage);
  SplashImage.Parent  := SplashPage.Surface;
  SplashImage.Left    := 0;
  SplashImage.Top     := 0;
  SplashImage.Width   := SplashPage.SurfaceWidth;
  SplashImage.Height  := SplashPage.SurfaceHeight;
  SplashImage.Stretch := True;
  SplashImage.Bitmap.LoadFromFile(ExpandConstant('{tmp}\valeniusWireguard.bmp'));

  { --- Page 2: Terms and Conditions --- }
  LicensePage := CreateCustomPage(SplashPage.ID,
    'Terms and Conditions',
    'Please read and accept our Terms and Conditions to continue.');

  { Custom Pascal-script pages are NOT auto-scaled by Inno's Per-Monitor-V2 wizard the
    way its own built-in pages are -- fixed pixel Left/Top/Width/Height here are design-time
    (96 DPI) values and must be run through ScaleX/ScaleY, or this page renders cramped and
    misaligned relative to the rest of the (correctly-scaled) wizard on a high-DPI monitor. }
  DescLabel := TLabel.Create(LicensePage);
  DescLabel.Parent   := LicensePage.Surface;
  DescLabel.AutoSize := False;
  DescLabel.Left     := 0;
  DescLabel.Top      := ScaleY(8);
  DescLabel.Width    := ScaleX(450);
  DescLabel.Height   := ScaleY(52);
  DescLabel.WordWrap := True;
  DescLabel.Caption  := 'By installing Valenius you agree to our Terms and Conditions. Click the link below to open the full document:';

  LinkLabel := TNewStaticText.Create(LicensePage);
  LinkLabel.Parent     := LicensePage.Surface;
  LinkLabel.Left       := 0;
  LinkLabel.Top        := DescLabel.Top + DescLabel.Height + ScaleY(10);
  LinkLabel.Caption    := 'https://www.valenius.com/clientagb.pdf';
  LinkLabel.Font.Color := clBlue;
  LinkLabel.Font.Style := [fsUnderline];
  LinkLabel.Cursor     := crHand;
  LinkLabel.OnClick    := @OpenLicenseLink;

  AcceptCheckbox := TCheckBox.Create(LicensePage);
  AcceptCheckbox.Parent  := LicensePage.Surface;
  AcceptCheckbox.Left    := 0;
  AcceptCheckbox.Top     := LinkLabel.Top + LinkLabel.Height + ScaleY(20);
  AcceptCheckbox.Width   := ScaleX(450);
  AcceptCheckbox.Caption := 'I accept the Terms and Conditions';
  AcceptCheckbox.OnClick := @AcceptCheckboxClick;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { In silent / very-silent mode skip the splash and T&C pages so the
    auto-update flow is never blocked by a disabled Next button. }
  Result := (PageID = wpWelcome) or
            (WizardSilent and
             ((PageID = SplashPage.ID) or (PageID = LicensePage.ID)));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = LicensePage.ID then
    WizardForm.NextButton.Enabled := WizardSilent or AcceptCheckbox.Checked;
end;

{ After uninstall completes, offer to remove all user data (configs, registration). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\Valenius');
    if DirExists(DataDir) then
    begin
      if MsgBox('Do you want to delete all Valenius data?' + #13#10 +
                'This includes VPN configurations and registration data.' + #13#10 + #13#10 +
                DataDir,
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;

{ Stop all services and kill the tray app before files are overwritten.
  Must happen here (ssInstall), before Inno Setup copies any files.
  install-service.ps1 (in [Run], after the copy) re-registers and restarts.

  Order matters:
    1. WireGuard tunnel services  -- wireguard.exe is bundled and will be replaced;
                                     it cannot be overwritten while any
                                     WireGuardTunnel$ service has it locked.
    2. Tray app                   -- holds no file locks but must exit before
                                     the trayapp directory is overwritten.
    3. Valenius service   -- holds its own EXE + DLL locks. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // 1. Stop every WireGuardTunnel$* service (safety net; UpdateChecker already
    //    disconnected cleanly, but handle the case where the user triggered an
    //    install manually or the service disconnect failed).
    //    Single quotes around the name prevent PowerShell treating $ as a variable.
    Exec('powershell.exe',
         '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
         '"Get-Service -Name ''WireGuardTunnel$*'' -ErrorAction SilentlyContinue |' +
         ' Stop-Service -Force -ErrorAction SilentlyContinue"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // 2. Kill the tray app.
    Exec('taskkill.exe', '/f /im ' + ExpandConstant('{#MyTrayExe}'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // 3. Stop the Valenius service so its EXE and DLLs are unlocked.
    //    Stop-Service -Force waits until the process has fully exited.
    Exec('powershell.exe',
         '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
         '"Stop-Service Valenius -Force -ErrorAction SilentlyContinue"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
