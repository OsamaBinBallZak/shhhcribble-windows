; Inno Setup script for Shhhcribble (Windows).
; Packages the self-contained publish output into a per-user installer that
; needs no admin rights — easy to hand to friends.

#define MyAppName "Shhhcribble"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Shhhcribble"
#define MyAppExeName "Shhhcribble.exe"

[Setup]
AppId={{8C2A4E91-7B3D-4F6A-9E12-5A0F3C7D9B41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Per-user install location — no administrator privileges required.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Shhhcribble-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\Shhhcribble\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
