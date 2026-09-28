; The MicMixer installer. scripts\Build-Installer.ps1 builds it from the published app:
;   ISCC.exe /DAppVersion=<version> /DPublishDir=<publish folder> /O<output folder> installer\MicMixer.iss
; plus /DSign and /Smicmixer=<sign command> when the release is signed.
;
; MicMixer's own updater runs it silently with two extra parameters:
;   /WAITPID=<id>          the MicMixer that started it; its files stay locked until it exits
;   /UPDATECLEANUP=<dir>   the download folder, which the restarted MicMixer deletes

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
; Windows and this installer recognise an existing MicMixer install by this id. Never change it.
AppId={{F566C357-2643-4949-B7FC-1D09C2AA7A3A}
AppName=MicMixer
AppVersion={#AppVersion}
AppVerName=MicMixer {#AppVersion}
AppPublisher=BenjiButten
AppPublisherURL=https://benjibutten.github.io/MicMixer/
AppSupportURL=https://github.com/benjibutten/MicMixer/issues
AppUpdatesURL=https://github.com/benjibutten/MicMixer/releases
VersionInfoVersion={#AppVersion}
; Program Files, because only a folder that nothing without administrator rights can
; change may start MicMixer as administrator at sign-in.
DefaultDirName={autopf}\MicMixer
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Uninstalling removes the startup entries of the account that runs it, which is the
; account that set them up in every case but an administrator uninstalling for someone else.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename=MicMixer-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\MicMixer\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MicMixer.exe
UninstallDisplayName=MicMixer
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; The Finished page and the update both start MicMixer again. Restart Manager must
; not start a second copy.
RestartApplications=no
UsePreviousTasks=no
#ifdef Sign
; Signs the installer and the uninstaller it writes into {app}.
SignTool=micmixer
#endif

[Tasks]
; Ticking it turns the setting on; unticked leaves the setting as it is in MicMixer.
; Setup therefore does not remember the tick, or a reinstall would turn the setting
; back on after the user turned it off in MicMixer.
Name: "runasadmin"; Description: "Run MicMixer as administrator, so its hotkeys also work while a game or program running as administrator has focus"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MicMixer"; Filename: "{app}\MicMixer.exe"; AppUserModelID: "BenjiButten.MicMixer"

[Run]
Filename: "{app}\MicMixer.exe"; Parameters: "--run-as-administrator"; Flags: waituntilterminated runascurrentuser; Tasks: runasadmin; Check: not WizardSilent
Filename: "{app}\MicMixer.exe"; Description: "Start MicMixer"; Tasks: runasadmin; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\MicMixer.exe"; Description: "Start MicMixer"; Tasks: not runasadmin; Flags: nowait postinstall skipifsilent runasoriginaluser
; After an update, MicMixer comes back with the rights it had: those of whoever started this installer.
Filename: "{app}\MicMixer.exe"; Parameters: "--update-cleanup ""{param:UPDATECLEANUP}"""; Flags: nowait runasoriginaluser; Check: IsUpdateFromMicMixer

[UninstallRun]
; The task that starts MicMixer as administrator at sign-in, for the account that uninstalls.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MicMixer ({username})"" /F"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\MicMixer-update-*"

[Code]
const
  SYNCHRONIZE = $00100000;
  EVENT_MODIFY_STATE = $0002;
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function OpenProcess(DesiredAccess: Cardinal; InheritHandle: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function OpenEvent(DesiredAccess: Cardinal; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';

function IsUpdateFromMicMixer: Boolean;
begin
  Result := ExpandConstant('{param:UPDATECLEANUP}') <> '';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ProcessId: Integer;
  Process: THandle;
  ExitRequest: THandle;
begin
  Result := '';
  ProcessId := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if ProcessId = 0 then
    Exit;

  // The MicMixer that started this update keeps running until Windows has approved
  // the installer, and exits when told so here. Name must match ExitForUpdateEventName
  // in GitHubUpdateService.cs.
  ExitRequest := OpenEvent(EVENT_MODIFY_STATE, False, 'MicMixer.ExitForUpdate.' + IntToStr(ProcessId));
  if ExitRequest <> 0 then
  begin
    SetEvent(ExitRequest);
    CloseHandle(ExitRequest);
  end;

  Process := OpenProcess(SYNCHRONIZE, False, ProcessId);
  if Process <> 0 then
  begin
    WaitForSingleObject(Process, 30000);
    CloseHandle(Process);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  // "Start with Windows" without administrator rights, unless it starts a copy of
  // MicMixer somewhere else.
  if RegQueryStringValue(HKCU, RunKey, 'MicMixer', Command)
    and (Pos(Uppercase(ExpandConstant('{app}\')), Uppercase(Command)) > 0) then
    RegDeleteValue(HKCU, RunKey, 'MicMixer');
end;
