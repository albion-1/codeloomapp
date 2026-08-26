#define MyAppName "Code Loom"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.8"
#endif
#define MyAppPublisher "Code Loom"
#define MyAppExeName "CodeLoom.exe"

[Setup]
AppId={{A1D80C76-86B3-4DBB-8FD7-71FE373D68E7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Code Loom
DefaultGroupName=Code Loom
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=CodeLoom-Setup-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Code Loom
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName=Code Loom
VersionInfoDescription=Code Loom Windows Installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\codeloomapp\bin\Release\net10.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Code Loom"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Code Loom"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Code Loom"; Flags: nowait postinstall skipifsilent
