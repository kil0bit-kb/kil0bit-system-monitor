; Kil0bit System Monitor - v3.0.0 Gold Inno Setup Script

[Setup]
AppId={{D3F1E2A3-4B5C-6D7E-8F9A-0B1C2D3E4F5G}
AppName=Kil0bit System Monitor
AppVersion={#AppVersion}
AppPublisher=KB - kil0bit
AppPublisherURL=https://github.com/kil0bit-kb/kil0bit-system-monitor
AppSupportURL=https://github.com/kil0bit-kb/kil0bit-system-monitor/issues
AppUpdatesURL=https://github.com/kil0bit-kb/kil0bit-system-monitor/releases
DefaultDirName={autopf}\Kil0bit System Monitor
DisableProgramGroupPage=yes
; Required for trusted path installation
PrivilegesRequired=admin
; Optional: Let user choose install location
DisableDirPage=no
OutputBaseFilename=Kil0bitSystemMonitor-v{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\Kil0bitSystemMonitor.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The source path will be where dotnet publish outputs the files
Source: "release-output\Kil0bitSystemMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "release-output\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Include the icon for the installer itself
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Kil0bit System Monitor"; Filename: "{app}\Kil0bitSystemMonitor.exe"
Name: "{autodesktop}\Kil0bit System Monitor"; Filename: "{app}\Kil0bitSystemMonitor.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Kil0bitSystemMonitor.exe"; Description: "{cm:LaunchProgram,Kil0bit System Monitor}"; Flags: nowait postinstall skipifsilent
