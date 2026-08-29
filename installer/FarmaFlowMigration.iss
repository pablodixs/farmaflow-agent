#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{F8A6A0B6-6F6B-4F95-A6E1-0B7540E02D15}
AppName=FarmaFlow Preparar migração
AppVersion={#MyAppVersion}
AppPublisher=FarmaFlow
DefaultDirName={autopf}\FarmaFlow Migration
DefaultGroupName=FarmaFlow
OutputDir=..\artifacts
OutputBaseFilename=FarmaFlow-Migracao-Setup
SetupIconFile=..\src\main\resources\ico.ico
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Files]
Source: "..\publish-migration\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FarmaFlow Preparar migração"; Filename: "{app}\FarmaFlowMigracaoSetup.exe"

[Run]
Filename: "{app}\FarmaFlowMigracaoSetup.exe"; Description: "Abrir FarmaFlow Preparar migração"; Flags: nowait postinstall skipifsilent
