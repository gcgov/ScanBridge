; Inno Setup script for ScanBridge.
; Build (from repo root, after dotnet publish):
;   iscc installer\ScanBridge.iss /DAppVersion=1.0.0 /DPublishDir=..\publish

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={{9C2E6F0A-3B7D-4A1E-9F5C-7B8A2D4E6F10}
AppName=ScanBridge
AppVersion={#AppVersion}
AppPublisher=ScanBridge contributors
AppPublisherURL=https://github.com/gcgov/desktop-scanning-client
DefaultDirName={localappdata}\Programs\ScanBridge
DisableProgramGroupPage=yes
; Per-user install: no admin rights required.
PrivilegesRequired=lowest
OutputBaseFilename=ScanBridge-{#AppVersion}-setup
SetupIconFile=..\src\ScanBridge\Resources\scanbridge.ico
UninstallDisplayIcon={app}\ScanBridge.exe
Compression=lzma2
SolidCompression=yes
; Ask a running ScanBridge to close before replacing files.
CloseApplications=yes
WizardStyle=modern

[Tasks]
Name: "autostart"; Description: "Start ScanBridge when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\ScanBridge"; Filename: "{app}\ScanBridge.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "ScanBridge"; ValueData: """{app}\ScanBridge.exe"" --minimized"; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ScanBridge.exe"; Description: "Launch ScanBridge now"; \
  Flags: nowait postinstall skipifsilent
; A silent install (Intune) closes a running ScanBridge before it replaces files.
; Start it again. On a first install, it opens the settings window, as an interactive install does.
Filename: "{app}\ScanBridge.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/im ScanBridge.exe /f"; Flags: runhidden; RunOnceId: "KillScanBridge"
