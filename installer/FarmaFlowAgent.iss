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
Root: HKCU; Subkey: "Software\Classes\.ffstation"; ValueType: string; ValueData: "FarmaFlowStationFile"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\FarmaFlowStationFile"; ValueType: string; ValueData: "FarmaFlow station configuration"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\FarmaFlowStationFile\DefaultIcon"; ValueType: string; ValueData: "{app}\FarmaFlowAgent.exe,0"
Root: HKCU; Subkey: "Software\Classes\FarmaFlowStationFile\shell\open\command"; ValueType: string; ValueData: "\"{app}\FarmaFlowAgent.exe\" \"%1\""

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  FindRec: TFindRec;
  SourceDirectory, SourcePath, DestinationPath: string;
  Count: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;
  SourceDirectory := AddBackslash(ExtractFilePath(ExpandConstant('{srcexe}')));
  Count := 0;
  if FindFirst(SourceDirectory + '*.ffstation', FindRec) then
  begin
    try
      repeat
        Count := Count + 1;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if Count <> 1 then
    exit;
  if FindFirst(SourceDirectory + '*.ffstation', FindRec) then
  begin
    try
      SourcePath := SourceDirectory + FindRec.Name;
      DestinationPath := AddBackslash(ExpandConstant('{app}')) + FindRec.Name;
      FileCopy(SourcePath, DestinationPath, False);
    finally
      FindClose(FindRec);
    end;
  end;
end;

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Run]
Filename: "{app}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Verificando Microsoft Edge WebView2..."; Flags: runhidden waituntilterminated; Check: FileExists(ExpandConstant('{app}\MicrosoftEdgeWebview2Setup.exe'))
Filename: "{app}\FarmaFlowAgent.exe"; Description: "Iniciar FarmaFlow Estação"; Flags: nowait postinstall skipifsilent
