; PowerLedger's installer (spec §13). Build it with installer\build.ps1, which publishes both programs first.
; Self-contained: both programs carry the .NET 10 runtime, so setup downloads nothing and the PC needs no .NET.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Compression
  #define Compression "lzma2/ultra64"
#endif
#ifndef PayloadBytes
  #define PayloadBytes "0"
#endif
#ifndef Architecture
  #define Architecture "both"
#endif
#define Publish "..\artifacts\publish"
#define ServiceName "PowerLedger"

[Setup]
AppId={{8E496D40-C77E-4F75-8C93-ED9D1E8BAF20}
AppName=PowerLedger
AppVersion={#AppVersion}
AppVerName=PowerLedger {#AppVersion}
AppPublisher=PowerLedger
DefaultDirName={autopf}\PowerLedger
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
#if Architecture == "x64"
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputBaseFilename=PowerLedger-{#AppVersion}-setup-x64
#elif Architecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
OutputBaseFilename=PowerLedger-{#AppVersion}-setup-arm64
#else
; A native build for each: x64 Windows gets the x64 build, Arm64 Windows 10 and 11 the Arm64 build (see [Files]).
; 32-bit Windows is refused with Inno Setup's own message.
ArchitecturesAllowed=x64os or arm64
ArchitecturesInstallIn64BitMode=x64os or arm64
OutputBaseFilename=PowerLedger-{#AppVersion}-setup
#endif
; Low on purpose: a compatibility mode (set by the user, or by Windows after an earlier failed start) makes setup see an
; older Windows, and a Windows 10 minimum here refused Windows 11 with "does not support the version of Windows".
; InitializeSetup checks the real build instead.
MinVersion=6.1sp1
; Inno Setup doesn't count files picked per architecture (the Checks in [Files]) toward the disk space it asks for, so
; build.ps1 passes the bigger build's size; without it the destination page claimed a few MB.
ExtraDiskSpaceRequired={#PayloadBytes}
AppMutex=PowerLedger.App
CloseApplications=yes
RestartApplications=no
OutputDir=output
; One solid stream with a 256 MB dictionary, so the service's copy of the runtime compresses against the App's. Setup
; needs about 256 MB of memory to unpack it. build.ps1 -Fast and the test builds pass lzma2/fast, which keeps its own
; small dictionary: about twice the size, compiled several times sooner.
Compression={#Compression}
SolidCompression=yes
#if Compression == "lzma2/ultra64"
LZMADictionarySize=262144
#endif
WizardStyle=modern
; The logo (assets\brand\make-brand.ps1): setup's own icon, the small image at the wizard's top right, and the large one on
; the Finished page, each in one file per display scale so Setup picks the sharpest.
SetupIconFile=..\assets\brand\PowerLedger.ico
WizardSmallImageFile=..\assets\brand\wizard-small-58.png,..\assets\brand\wizard-small-72.png,..\assets\brand\wizard-small-87.png,..\assets\brand\wizard-small-116.png,..\assets\brand\wizard-small-159.png
WizardImageFile=..\assets\brand\wizard-large-202.png,..\assets\brand\wizard-large-252.png,..\assets\brand\wizard-large-303.png,..\assets\brand\wizard-large-404.png,..\assets\brand\wizard-large-505.png
UninstallDisplayIcon={app}\PowerLedger.exe
UninstallDisplayName=PowerLedger
SetupLogging=yes
UninstallLogging=yes
; Before a public release: sign the installer and the uninstaller (spec §11), e.g.
; SignTool=signtool sign /fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 $f

[Files]
; Each build carries its own runtime; the Check installs the one for this PC. x64 comes first, so x64 PCs, most of
; them, unpack only their own build; Arm64 PCs read through the x64 build first, a few seconds. One stream, rather than
; a chunk per build, keeps the installer 12 MB smaller, because the builds share many identical files.
#if Architecture != "arm64"
Source: "{#Publish}\win-x64\App\*"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Publish}\win-x64\Service\*"; DestDir: "{app}\Service"; Check: not IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#if Architecture != "x64"
Source: "{#Publish}\win-arm64\App\*"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Publish}\win-arm64\Service\*"; DestDir: "{app}\Service"; Check: IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{autoprograms}\PowerLedger"; Filename: "{app}\PowerLedger.exe"; Comment: "How much power this PC uses, and what it costs"

[Run]
; The first window of a new install is the wizard; the App also turns on starting with Windows for this user.
Filename: "{app}\PowerLedger.exe"; Description: "Open PowerLedger"; Flags: postinstall nowait skipifsilent runasoriginaluser
; An update the App started (spec §13) passes /UPDATE=1: the App opens again, as the user who started setup rather than
; as the administrator setup runs as.
Filename: "{app}\PowerLedger.exe"; Flags: nowait runasoriginaluser; Check: IsUpdate

[Code]
{ The App starts setup with /UPDATE=1 when the user chooses "Restart to update" (spec §13). }
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function RunHidden(const FileName, Params: string): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

{ Every sc and net call is logged with its exit code. }
function Sc(const Params: string): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
  Log(Format('sc %s -> %d', [Params, Result]));
end;

function Net(const Params: string): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\net.exe'), Params);
  Log(Format('net %s -> %d', [Params, Result]));
end;

function Netsh(const Params: string): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\netsh.exe'), Params);
  Log(Format('netsh %s -> %d', [Params, Result]));
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

{ net stop waits until the service has stopped, so its files can be replaced; a stopped service is not an error. }
procedure StopService;
begin
  if ServiceExists then
    Net('stop {#ServiceName}');
end;

function ServiceExecutable: string;
begin
  Result := ExpandConstant('{app}\Service\PowerLedger.Service.exe');
end;

{ Households (design §3): the other PCs in a household reach the service's listener, inbound TCP to the service's program,
  on Private networks only and from the local subnet only (plan 0.8). Deleting it first replaces the rule an earlier install
  added, so an update never doubles it. A failure is only logged: the PCs can still pair and sync through the server. }
procedure AddFirewallRule;
begin
  Netsh('advfirewall firewall delete rule name="PowerLedger households"');
  if Netsh('advfirewall firewall add rule name="PowerLedger households" dir=in action=allow program="' + ServiceExecutable +
      '" protocol=TCP profile=private remoteip=localsubnet') <> 0 then
    Log('The households firewall rule could not be added.');
end;

procedure RemoveFirewallRule;
begin
  Netsh('advfirewall firewall delete rule name="PowerLedger households"');
end;

{ The quoted image path keeps Windows from starting C:\Program.exe in the service's place. }
procedure RegisterService;
var
  Path: string;
  Code: Integer;
begin
  Path := '"\"' + ServiceExecutable + '\""';
  if ServiceExists then
    Code := Sc('config {#ServiceName} binPath= ' + Path + ' start= auto obj= LocalSystem')
  else
    Code := Sc('create {#ServiceName} binPath= ' + Path + ' start= auto obj= LocalSystem DisplayName= "PowerLedger"');
  if Code <> 0 then
  begin
    SuppressibleMsgBox('The PowerLedger service could not be registered (code ' + IntToStr(Code) + '). If the code is 1072, ' +
      'close the Services window or restart Windows, then run this setup again.', mbError, MB_OK, IDOK);
    Exit;
  end;
  Sc('description {#ServiceName} "Records how much power this PC uses."');
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  Sc('failureflag {#ServiceName} 1');
  { A failed start is only logged: the App shows the service as not running and offers to start it. }
  Net('start {#ServiceName}');
end;

const
  EVENT_MODIFY_STATE = $0002;

{ Setup is a 32-bit program (the script sets no SetupArchitecture), so a handle fits in a Cardinal; Inno Setup 6 has no THandle. }
function PLOpenEvent(Access: Cardinal; Inherit: Longint; Name: string): Cardinal; external 'OpenEventW@kernel32.dll stdcall';
function PLSetEvent(Handle: Cardinal): Longint; external 'SetEvent@kernel32.dll stdcall';
function PLCloseHandle(Handle: Cardinal): Longint; external 'CloseHandle@kernel32.dll stdcall';

{ Asks a running App in this session to exit, as its tray's Exit does (the App listens on this event), and waits up to
  10 s for it to go. If it stays, the AppMutex check that follows asks the user to close it. }
procedure CloseApp;
var
  Event: Cardinal;
  Waited: Integer;
begin
  Event := PLOpenEvent(EVENT_MODIFY_STATE, 0, 'Local\PowerLedger.App.Exit');
  if Event = 0 then
    Exit;
  PLSetEvent(Event);
  PLCloseHandle(Event);
  Waited := 0;
  while CheckForMutexes('PowerLedger.App') and (Waited < 10000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Log(Format('Asked PowerLedger to exit; waited %d ms.', [Waited]));
end;

function PLSetEnvironmentVariable(Name: string; Value: string): Longint; external 'SetEnvironmentVariableW@kernel32.dll stdcall';

const
  { Windows 10 version 1809, the oldest the App and the service are built and tested for. }
  OldestBuild = 17763;

{ The Windows build as the registry has it, which a compatibility mode doesn't change; 0 when it can't be read. }
function RealWindowsBuild: Integer;
var
  Root: Integer;
  Build: string;
begin
  Result := 0;
  if IsWin64 then
    Root := HKLM64
  else
    Root := HKLM;
  if RegQueryStringValue(Root, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'CurrentBuildNumber', Build) then
    Result := StrToIntDef(Build, 0);
end;

function InitializeSetup: Boolean;
var
  Build: Integer;
begin
  Build := RealWindowsBuild;
  if (Build > 0) and (Build < OldestBuild) then
  begin
    SuppressibleMsgBox('PowerLedger needs Windows 10 version 1809 or later, or Windows 11. This PC has Windows build ' +
      IntToStr(Build) + '.', mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  if GetWindowsVersion < $0A000000 then
    Log(Format('Setup runs in a compatibility mode (it sees Windows %.8x; the build is %d). Going on without it.', [GetWindowsVersion, Build]));
  { Programs setup starts (sc, net, netsh, the App) would inherit the compatibility layer through this variable. }
  PLSetEnvironmentVariable('__COMPAT_LAYER', '');
  Result := True;
  { Setup's own AppMutex check comes next. }
  CloseApp;
end;

var
  { PrepareToInstall stopped the service, and ssPostInstall hasn't yet registered and started it again. }
  ServiceDown: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  StopService;
  ServiceDown := True;
  Result := '';
end;

{ The build [Files] installed on this PC. }
function BuildName: string;
begin
  if IsArm64 then
    Result := 'Arm64'
  else
    Result := 'x64';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    Log('Installed the ' + BuildName + ' build.');
    AddFirewallRule;
    RegisterService;
    ServiceDown := False;
  end;
end;

{ A setup that stopped the service but never got to start it again (an error, or Cancel, during the copy) starts it, and
  reopens the App if the App started this update (spec §13), rather than leave both off until Windows restarts. Whatever
  the copy left in place is what starts; if that can't run, the App's next start offers the update again. }
procedure DeinitializeSetup;
var
  Code: Integer;
begin
  if not ServiceDown then
    Exit;
  Log('Setup ended before it finished; starting the service, and the App for an update, again.');
  if ServiceExists then
    Net('start {#ServiceName}');
  if IsUpdate and FileExists(ExpandConstant('{app}\PowerLedger.exe')) then
    try
      ExecAsOriginalUser(ExpandConstant('{app}\PowerLedger.exe'), '', '', SW_SHOWNORMAL, ewNoWait, Code);
    except
      Log('Could not open the App again: ' + GetExceptionMessage);
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: string;
begin
  { The user has confirmed the uninstall, and Uninstall's own AppMutex check comes next. }
  if CurUninstallStep = usAppMutexCheck then
    CloseApp;
  if CurUninstallStep = usUninstall then
  begin
    StopService;
    Sc('delete {#ServiceName}');
    RemoveFirewallRule;
    { The event source the service creates; it goes after the service stops, since stopping writes an event. }
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\EventLog\Application\PowerLedger');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerLedger');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\PowerLedger');
    if DirExists(Data) and (SuppressibleMsgBox('Keep your PowerLedger history? It is in ' + Data + '.' + #13#10 +
        'Choose No to delete it.', mbConfirmation, MB_YESNO or MB_DEFBUTTON1, IDYES) = IDNO) then
      DelTree(Data, True, True, True);
  end;
end;
