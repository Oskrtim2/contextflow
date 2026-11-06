; ContextFlow commercial installer (Inno Setup 6+)
; Build ContextFlow-windows first with:
;   .\scripts\publish.ps1 -Runtime win-x64
; Then compile this script with ISCC.exe.
;
; Per-user installation avoids an elevation prompt while still supporting
; machines where the current user cannot write to Program Files.

#define MyAppName "ContextFlow"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ContextFlow"
#define MyAppExeName "ContextFlow.exe"
#define MyAppId "{{8E6A9B70-30F0-4A49-BA1E-2A77F0C4C1D7}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ContextFlow
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=ContextFlow-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\ContextFlow-{#MyAppVersion}-win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\ContextFlow-{#MyAppVersion}-win-x64\SHA256SUMS.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\artifacts\ContextFlow-{#MyAppVersion}-win-x64\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\ContextFlow User Guide"; Filename: "{app}\licenses\USER_GUIDE.md"; Flags: createonlyiffileexists
Name: "{group}\Third-Party Notices"; Filename: "{app}\licenses\THIRD_PARTY_NOTICES.md"; Flags: createonlyiffileexists
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
