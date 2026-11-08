; SsdVirtual installer (Inno Setup 6). Built by CI:
;   ISCC /DAppVersion=1.0.0 /DSourceDir=<publish folder> /DOutputDir=<dist> installer\SsdVirtual.iss
; The publish folder contains SsdVirtual.exe, SsdVirtualService.exe and winfsp.msi.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\SsdVirtual"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{8D5B1C9E-6F2A-4B7E-9C3D-5A1E2F3B4C6D}
AppName=SsdVirtual
AppVersion={#AppVersion}
AppVerName=SsdVirtual {#AppVersion}
AppPublisher=SsdVirtual
DefaultDirName={autopf}\SsdVirtual
DefaultGroupName=SsdVirtual
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=SsdVirtual-Setup
SetupIconFile=..\src\SsdVirtual.Desktop\Assets\SsdVirtual.ico
UninstallDisplayIcon={app}\SsdVirtual.exe
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "trayautostart"; Description: "Start the SsdVirtual tray app when a user signs in"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "winfsp.msi,*.pdb,install-service.ps1,uninstall-service.ps1,README-portable.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\winfsp.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Dirs]
Name: "{commonappdata}\SsdVirtual"
Name: "{commonappdata}\SsdVirtual\logs"

[Icons]
Name: "{group}\SsdVirtual"; Filename: "{app}\SsdVirtual.exe"
Name: "{group}\Uninstall SsdVirtual"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SsdVirtual"; Filename: "{app}\SsdVirtual.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SsdVirtual"; ValueData: """{app}\SsdVirtual.exe"" --tray"; Tasks: trayautostart; Flags: uninsdeletevalue

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; StatusMsg: "Installing the WinFsp file system driver..."; Check: NeedsWinFsp; Flags: waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "create SsdVirtualService binPath= ""{app}\SsdVirtualService.exe"" start= auto DisplayName= ""SsdVirtual Service"""; StatusMsg: "Registering the SsdVirtual service..."; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "config SsdVirtualService binPath= ""{app}\SsdVirtualService.exe"" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description SsdVirtualService ""Mounts SsdVirtual cloud drives (Google Drive, TeraBox) as local disks and keeps them in sync."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure SsdVirtualService reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start SsdVirtualService"; StatusMsg: "Starting the SsdVirtual service..."; Flags: runhidden waituntilterminated
Filename: "{app}\SsdVirtual.exe"; Description: "Launch SsdVirtual"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop SsdVirtualService"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\timeout.exe"; Parameters: "/t 5 /nobreak"; Flags: runhidden waituntilterminated; RunOnceId: "WaitStop"
Filename: "{sys}\sc.exe"; Parameters: "delete SsdVirtualService"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM SsdVirtual.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillTray"

[Code]
function NeedsWinFsp: Boolean;
var
  Dir: String;
begin
  Result := not RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { Upgrade: stop the service (flushes caches and unmounts drives) and the tray app before files are replaced. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop SsdVirtualService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(4000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SsdVirtual.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('SsdVirtual was removed. Your cloud files were not touched. Local cache and settings remain in ' +
      ExpandConstant('{commonappdata}\SsdVirtual') + ' and the cache folder (default C:\SsdVirtualCache) so nothing that was not uploaded yet is lost. Delete them manually if you no longer need them.' + #13#10#13#10 +
      'WinFsp stays installed because other programs may use it; remove it from Apps & features if you want.',
      mbInformation, MB_OK);
end;
