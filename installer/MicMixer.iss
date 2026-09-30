; The MicMixer installer. scripts\Build-Installer.ps1 builds it from the published app and
; VB-Audio's VB-CABLE driver package:
;   ISCC.exe /DAppVersion=<version> /DPublishDir=<publish folder> /DVBCableDir=<VB-CABLE files> /O<output folder> installer\MicMixer.iss
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
#ifndef VBCableDir
  #error Pass /DVBCableDir=<folder with the extracted VB-CABLE driver package>; scripts\Build-Installer.ps1 does.
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
; Setup picks the image that best fits the display scaling.
WizardImageFile=WizardImage100.png,WizardImage150.png,WizardImage200.png
WizardSmallImageFile=WizardSmallImage100.png,WizardSmallImage150.png,WizardSmallImage200.png
DisableWelcomePage=no
InfoBeforeFile=BeforeInstall.txt
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

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nMicMixer mixes your microphone and your music into one virtual microphone, so your game or voice chat hears exactly what you let through.%n%nIt is free and open source, and every release is built from the public code on GitHub.
WizardInfoBefore=Before you install
InfoBeforeLabel=What gets installed, what MicMixer connects to, and its license.
FinishedLabel=Setup has finished installing [name] on your computer.%n%nThe first time MicMixer starts, a setup guide helps you pick your microphone and the virtual cable.

[Tasks]
; Ticking it turns the setting on; unticked leaves the setting as it is in MicMixer.
; Setup therefore does not remember the tick, or a reinstall would turn the setting
; back on after the user turned it off in MicMixer.
Name: "runasadmin"; Description: "Run MicMixer as administrator, so its hotkeys also work while a game or program running as administrator has focus"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VBCableDir}\*"; DestDir: "{tmp}\vbcable"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\MicMixer"; Filename: "{app}\MicMixer.exe"; AppUserModelID: "BenjiButten.MicMixer"

[Run]
; As the account that started Setup, not the one that approved it: they differ when a
; standard account enters an administrator's password, and MicMixer and its settings
; belong to the first. With the setting on, MicMixer then asks for elevation itself.
Filename: "{app}\MicMixer.exe"; Parameters: "--run-as-administrator"; Flags: waituntilterminated runasoriginaluser; Tasks: runasadmin; Check: not WizardSilent
Filename: "{app}\MicMixer.exe"; Description: "Start MicMixer"; Flags: nowait postinstall skipifsilent runasoriginaluser
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
  RenderKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render';
  // PKEY_DeviceInterface_FriendlyName: the driver's name, which a user cannot rename.
  InterfaceNameValue = '{b3f8fa53-0004-438e-9003-51a46e139bfc},6';
  CableInterfaceName = 'VB-Audio Virtual Cable';
  DEVICE_STATEMASK = $F;
  DEVICE_STATE_NOTPRESENT = 4;
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;
  SWP_NOSIZE = $0001;
  SWP_NOMOVE = $0002;

var
  CablePage: TInputOptionWizardPage;
  CableWasInstalled: Boolean;
  CableNeedsRestart: Boolean;
  WizardRaised: Boolean;

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
function SetWindowPos(Window: HWND; InsertAfter: Integer; X, Y, Width, Height: Integer; Flags: Cardinal): Boolean;
  external 'SetWindowPos@user32.dll stdcall';

procedure CurPageChanged(CurPageID: Integer);
begin
  if WizardRaised or WizardSilent then
    Exit;
  WizardRaised := True;

  // After the UAC prompt the wizard can open behind the window it was started from,
  // since Windows lets only the process the user last used take the focus. Passing
  // through the topmost band puts it on top anyway; the focus follows when allowed.
  SetWindowPos(WizardForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
  SetWindowPos(WizardForm.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
  BringToFrontAndRestore;
end;

// True when Windows has a VB-CABLE playback end that is not left over from an
// uninstalled driver.
function IsVBCableInstalled: Boolean;
var
  Endpoints: TArrayOfString;
  I: Integer;
  Name: String;
  State: Cardinal;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM64, RenderKey, Endpoints) then
    Exit;

  for I := 0 to GetArrayLength(Endpoints) - 1 do
  begin
    if RegQueryStringValue(HKLM64, RenderKey + '\' + Endpoints[I] + '\Properties', InterfaceNameValue, Name)
      and (Name = CableInterfaceName)
      and RegQueryDWordValue(HKLM64, RenderKey + '\' + Endpoints[I], 'DeviceState', State)
      and ((State and DEVICE_STATEMASK) <> DEVICE_STATE_NOTPRESENT) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure CableOptionClickCheck(Sender: TObject);
begin
  CablePage.CheckListBox.ItemEnabled[1] := CablePage.Values[0];
end;

procedure InitializeWizard;
begin
  CableWasInstalled := IsVBCableInstalled;

  CablePage := CreateInputOptionPage(wpSelectTasks,
    'Virtual audio cable',
    'MicMixer sends your mix through a virtual audio cable.',
    'Your game or chat app picks up the mix from a virtual audio cable, which it sees as a microphone. '
      + 'This installer can install VB-CABLE for you.' + #13#10#13#10
      + 'VB-CABLE is made by VB-Audio Software, not by MicMixer. The origin of VB-CABLE: www.vb-cable.com. '
      + 'VB-CABLE is a donationware, all participations are welcome.' + #13#10#13#10
      + 'Untick it if you already have a virtual audio cable or would rather install one yourself. '
      + 'Uninstalling MicMixer leaves VB-CABLE in place. Windows sometimes makes a new audio device '
      + 'the default one; if you hear nothing afterwards, choose your speakers again in the Windows sound settings.',
    False, False);
  CablePage.Add('Install VB-CABLE');
  CablePage.Add('Name its two ends "MicMixer Input" and "MicMixer Output", so they are easy to find in your game and chat app');
  CablePage.Values[0] := True;
  CablePage.Values[1] := True;
  CablePage.CheckListBox.OnClickCheck := @CableOptionClickCheck;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CablePage.ID) and CableWasInstalled;
end;

procedure InstallCable;
var
  CableFolder: String;
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing VB-CABLE from VB-Audio Software...';
  ExtractTemporaryFiles('{tmp}\vbcable\*');
  CableFolder := ExpandConstant('{tmp}\vbcable');
  // VB-Audio documents no exit codes, so whether it worked is read from Windows afterwards.
  Exec(CableFolder + '\VBCABLE_Setup_x64.exe', '-i -h', CableFolder, SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // VB-CABLE usually works at once. When Windows shows no cable yet, it needs a restart.
  CableNeedsRestart := not IsVBCableInstalled;
end;

procedure NameCable;
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Naming the virtual cable...';
  Exec(ExpandConstant('{app}\MicMixer.exe'), '--name-virtual-cable', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Updates run silently and never touch the cable.
  if WizardSilent or CableWasInstalled or not CablePage.Values[0] then
    Exit;

  // Setup asks NeedRestart right after ssInstall, before the files are copied, so
  // the cable is installed here to know by then. Naming it needs MicMixer.exe,
  // which is in place only at ssPostInstall, and a cable that still needs a
  // restart has no ends to name.
  if CurStep = ssInstall then
    InstallCable
  else if (CurStep = ssPostInstall) and CablePage.Values[1] and not CableNeedsRestart then
    NameCable;
end;

function NeedRestart: Boolean;
begin
  Result := CableNeedsRestart;
end;

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
