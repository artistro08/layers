[Setup]
AppId={{8B4D6C21-5E3A-4C7E-9F2B-7A1D0E5C3B94}
AppName=Layers
AppVersion=1.0.2
DefaultDirName={localappdata}\Layers
DefaultGroupName=Layers
DisableProgramGroupPage=yes
DisableDirPage=yes
UninstallDisplayIcon={app}\Layers.exe
OutputDir=Output
OutputBaseFilename=layers-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user, so no UAC prompt.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\target\release\Layers.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\NOTICE-fluentui.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Layers"; Filename: "{app}\Layers.exe"

[Tasks]
Name: "startup"; Description: "Start Layers when I sign in"; GroupDescription: "Additional options:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Layers"; ValueData: """{app}\Layers.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Layers.exe"; Description: "Start Layers now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close the running instance so the exe is not locked during uninstall.
Filename: "taskkill.exe"; Parameters: "/F /IM Layers.exe"; Flags: runhidden skipifdoesntexist
