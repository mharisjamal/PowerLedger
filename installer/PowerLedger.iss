; PowerLedger's installer (spec §13). Build it with installer\build.ps1, which publishes both programs first.
; Framework-dependent: the .NET 10 Desktop Runtime is downloaded when it is missing, so this stays small.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define Publish "..\artifacts\publish"
#define ServiceName "PowerLedger"
#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=PowerLedger.App
CloseApplications=yes
RestartApplications=no
OutputDir=output
OutputBaseFilename=PowerLedger-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PowerLedger.exe
UninstallDisplayName=PowerLedger
SetupLogging=yes
; Before a public release: sign the installer and the uninstaller (spec §11), e.g.
; SignTool=signtool sign /fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 $f

[Files]
Source: "{#Publish}\App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Publish}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PowerLedger"; Filename: "{app}\PowerLedger.exe"; Comment: "How much power this PC uses, and what it costs"

[Run]
; The first window of a new install is the wizard; the App also turns on starting with Windows for this user.
Filename: "{app}\PowerLedger.exe"; Description: "Open PowerLedger"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
var
  RuntimePage: TDownloadWizardPage;

function RunHidden(const FileName, Params: string): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function Sc(const Params: string): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

{ net stop waits until the service has stopped, so its files can be replaced; a stopped service is not an error. }
procedure StopService;
begin
  if ServiceExists then
    RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
end;

function ServiceExecutable: string;
begin
  Result := ExpandConstant('{app}\Service\PowerLedger.Service.exe');
end;

{ The image path is quoted: the App checks that the pipe's server is this executable (Plan D3). }
procedure RegisterService;
var
  Path: string;
begin
  Path := '"\"' + ServiceExecutable + '\""';
  if ServiceExists then
    Sc('config {#ServiceName} binPath= ' + Path + ' start= auto obj= LocalSystem')
  else
    Sc('create {#ServiceName} binPath= ' + Path + ' start= auto obj= LocalSystem DisplayName= "PowerLedger"');
  Sc('description {#ServiceName} "Records how much power this PC uses."');
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  Sc('failureflag {#ServiceName} 1');
  RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
end;

{ The Desktop Runtime is present when dotnet's shared folder holds a 10.x version of it. }
function DesktopRuntimeInstalled: Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found);
  if Result then
    FindClose(Found);
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateDownloadPage('Getting .NET', 'PowerLedger needs the .NET 10 Desktop Runtime, which is being downloaded from Microsoft.', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or DesktopRuntimeInstalled then
    Exit;
  RuntimePage.Clear;
  RuntimePage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-win-x64.exe', '');
  RuntimePage.Show;
  try
    try
      RuntimePage.Download;
    except
      if not RuntimePage.AbortedByUser then
        SuppressibleMsgBox('The .NET 10 Desktop Runtime could not be downloaded: ' + GetExceptionMessage + #13#10 +
          'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.', mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
  finally
    RuntimePage.Hide;
  end;
  if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code)
     or ((Code <> 0) and (Code <> 3010)) then
  begin
    SuppressibleMsgBox('The .NET 10 Desktop Runtime could not be installed (code ' + IntToStr(Code) + ').', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ Silent installs never reach the Ready page, so they need the runtime already. }
function InitializeSetup: Boolean;
begin
  Result := True;
  if WizardSilent and not DesktopRuntimeInstalled then
  begin
    Log('The .NET 10 Desktop Runtime is missing; a silent install cannot fetch it.');
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  StopService;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService;
    Sc('delete {#ServiceName}');
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
