#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{E8F1F8CC-8C04-4ED6-98A1-546C8F98F236}
AppName=FarmaFlow Estação
AppVersion={#MyAppVersion}
AppPublisher=FarmaFlow
DefaultDirName={localappdata}\Programs\FarmaFlow Estação
DefaultGroupName=FarmaFlow
OutputDir=..\artifacts
OutputBaseFilename=FarmaFlow-Estacao-Setup
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
Name: "{group}\FarmaFlow Estação"; Filename: "{app}\FarmaFlowAgent.exe"
Name: "{autodesktop}\FarmaFlow"; Filename: "{app}\FarmaFlowAgent.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FarmaFlowAgent"; ValueData: "\"{app}\FarmaFlowAgent.exe\""; Flags: uninsdeletevalue

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Run]
Filename: "{app}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Verificando Microsoft Edge WebView2..."; Flags: runhidden waituntilterminated; Check: FileExists(ExpandConstant('{app}\MicrosoftEdgeWebview2Setup.exe'))
Filename: "{app}\FarmaFlowAgent.exe"; Description: "Iniciar FarmaFlow Estação"; Flags: nowait postinstall skipifsilent
