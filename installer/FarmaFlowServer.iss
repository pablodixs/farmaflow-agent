#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{C7D46977-A934-471D-A3CA-6814B7DB4B62}
AppName=FarmaFlow Server
AppVersion={#MyAppVersion}
AppPublisher=FarmaFlow
DefaultDirName={autopf}\FarmaFlow Server
DefaultGroupName=FarmaFlow
OutputDir=..\artifacts
OutputBaseFilename=FarmaFlow-Server-Setup
SetupIconFile=..\src\main\resources\ico.ico
UninstallDisplayIcon={app}\FarmaFlowServerHost.exe
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Files]
Source: "..\publish-server\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "bootstrap-server.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "uninstall-server.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "activate-server.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "export-recovery-key.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Icons]
Name: "{group}\Certificado do FarmaFlow Server"; Filename: "notepad.exe"; Parameters: """{commonappdata}\FarmaFlow\Server\certificate.sha256.txt"""
Name: "{group}\Ativar FarmaFlow após migração"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\activate-server.ps1"" -InstallDirectory ""{app}"""
Name: "{group}\Reparar FarmaFlow Server"; Filename: "{app}\FarmaFlowServerSetup.exe"; Parameters: "--repair"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\bootstrap-server.ps1"" -InstallDirectory ""{app}"""; StatusMsg: "Inicializando PostgreSQL e serviços locais..."; Flags: runhidden waituntilterminated
Filename: "{app}\FarmaFlowServerSetup.exe"; Description: "Configurar este servidor"; StatusMsg: "Abrindo assistente do servidor..."; Flags: waituntilterminated postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\uninstall-server.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "StopFarmaFlowServices"
