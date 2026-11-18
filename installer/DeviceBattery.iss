; Inno Setup script for Device Battery (https://jrsoftware.org/isinfo.php).
; Per-user install by default: no administrator rights needed, installs to %LOCALAPPDATA%\Programs.
; The wizard also offers "Install for all users" (Program Files, needs administrator rights).
; Build with installer\build-installer.ps1 (publishes the app, then runs ISCC).

#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif
#define Root ".."
#define ExeName "DeviceBattery.exe"

[Setup]
AppId={{B5F0A3C1-7D2E-4E8A-9C34-6A1B8E2D5F70}
AppName=Device Battery
AppVersion={#AppVersion}
AppVerName=Device Battery {#AppVersion}
AppPublisher=Device Battery
DefaultDirName={autopf}\Device Battery
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#Root}\dist
OutputBaseFilename=DeviceBattery-Setup-{#AppVersion}
SetupIconFile={#Root}\src\app.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName=Device Battery
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
english.AutoStart=Start Device Battery when I sign in to Windows
turkish.AutoStart=Windows'a giriş yaptığımda Device Battery'yi başlat
english.RunNow=Start Device Battery now
turkish.RunNow=Device Battery'yi şimdi başlat

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"

[Files]
Source: "{#Root}\dist\self-contained\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Device Battery"; Filename: "{app}\{#ExeName}"

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:RunNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#ExeName}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitApp"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure QuitRunningApp();
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\{#ExeName}')) then
    Exec(ExpandConstant('{app}\{#ExeName}'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function InstalledRunValue(): String;
begin
  Result := '"' + ExpandConstant('{app}\{#ExeName}') + '"';
end;

{ Removes the startup entry only if it points at this installed copy, so a portable copy's
  "Start with Windows" setting is left alone. }
procedure RemoveOwnAutostart();
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, RunKey, 'DeviceBattery', Value) and (CompareText(Value, InstalledRunValue()) = 0) then
    RegDeleteValue(HKCU, RunKey, 'DeviceBattery');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    QuitRunningApp();  { upgrade: close the old version so its files can be replaced }
  if CurStep = ssPostInstall then
  begin
    { Same value the app's own "Start with Windows" menu item writes, so the two stay in sync. }
    if WizardIsTaskSelected('autostart') then
      RegWriteStringValue(HKCU, RunKey, 'DeviceBattery', InstalledRunValue())
    else
      RemoveOwnAutostart();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveOwnAutostart();
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\DeviceBattery');            { saved settings }
    DelTree(ExpandConstant('{localappdata}\DeviceBattery'), True, True, True); { error log }
  end;
end;

