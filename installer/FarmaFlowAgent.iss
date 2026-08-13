#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{E8F1F8CC-8C04-4ED6-98A1-546C8F98F236}
AppName=FarmaFlow Agent
AppVersion={#MyAppVersion}
AppPublisher=FarmaFlow
DefaultDirName={localappdata}\Programs\FarmaFlow Agent
DefaultGroupName=FarmaFlow
OutputDir=..\artifacts
OutputBaseFilename=FarmaFlowAgent-Setup
SetupIconFile=..\src\main\resources\ico.ico
UninstallDisplayIcon={app}\FarmaFlowAgent.exe
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FarmaFlow Agent"; Filename: "{app}\FarmaFlowAgent.exe"
Name: "{autodesktop}\FarmaFlow Agent"; Filename: "{app}\FarmaFlowAgent.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FarmaFlowAgent"; ValueData: "\"{app}\FarmaFlowAgent.exe\""; Flags: uninsdeletevalue

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Run]
Filename: "{app}\FarmaFlowAgent.exe"; Description: "Iniciar FarmaFlow Agent"; Flags: nowait postinstall skipifsilent
