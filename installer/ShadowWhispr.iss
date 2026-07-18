; Inno Setup script for ShadowWhispr.
; Builds a per-user Setup.exe (no admin prompt) that lays down the self-contained
; app plus the speech-to-text scripts. The heavy Python/Parakeet speech engine is
; still downloaded on first launch on the user's PC, exactly like a source run.
;
; Compile with:
;   ISCC.exe /DMyAppVersion=1.2.3 /DSourceDir=..\build\ShadowWhispr installer\ShadowWhispr.iss
; scripts\build-installer.ps1 does this for you.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\ShadowWhispr"
#endif
#ifndef ScriptsDir
  #define ScriptsDir "..\scripts"
#endif
#ifndef PythonDir
  #define PythonDir "..\build\python"
#endif
#ifndef OutputDir
  #define OutputDir "..\build\installer"
#endif

#define MyAppName "ShadowWhispr"
#define MyAppPublisher "shadowdoggie"
#define MyAppExeName "ShadowWhispr.exe"
#define MyAppUrl "https://github.com/shadowdoggie/ShadowWhispr"

[Setup]
; Keep AppId stable forever so upgrades replace instead of duplicate-install.
AppId={{8F3A1C2E-5B4D-4E6F-9A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; lowest = install per-user, no UAC prompt, into a writable folder so the
; first-run speech setup can create its .venv next to the app.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ShadowWhispr-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
; ShadowWhispr can be running with no window at all (it keeps listening from the
; system tray), so without this an upgrade would fail on a locked exe while the
; user is certain they closed it. The name matches the single-instance mutex the
; app creates at startup; see App.xaml.cs.
AppMutex=ShadowWhispr.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The self-contained publish output (ShadowWhispr.exe, .NET runtime, DLLs, stt\).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; The PowerShell scripts, so first-run speech setup can run in place.
Source: "{#ScriptsDir}\*"; DestDir: "{app}\scripts"; Flags: recursesubdirs createallsubdirs ignoreversion
; ShadowWhispr's own Python 3.12. Shipping it means the user never installs
; Python, and setup never has to pick between the ones already on the machine.
Source: "{#PythonDir}\*"; DestDir: "{app}\python"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Set up speech (run once before first use)"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\setup-stt.ps1"""; Comment: "Downloads the local speech engine. Needs Python 3.12 and an NVIDIA GPU."
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the first-run speech environment the app created next to itself.
Type: filesandordirs; Name: "{app}\.venv"
Type: filesandordirs; Name: "{app}\speech-model"
; Byte-code caches written next to the bundled runtime are not tracked files,
; so remove the whole folder rather than leaving it behind half-empty.
Type: filesandordirs; Name: "{app}\python"
Type: files; Name: "{app}\setup-log.txt"
Type: files; Name: "{app}\app-log.txt"
Type: files; Name: "{app}\app-log.old.txt"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the optional "start with Windows" entry the app writes when the
    // user ticks that box. Left behind, Windows would keep trying to launch a
    // deleted executable at every login.
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'ShadowWhispr');
  end;
end;
