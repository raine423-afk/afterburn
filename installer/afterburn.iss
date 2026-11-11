; Inno Setup script for the Afterburn Windows installer.
; Built by .github/workflows/build.yaml; to build locally, publish to ..\publish first, then run:
;   iscc installer\afterburn.iss /DAppVersion=1.0.0

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F0B8E2A-3C4D-4E7B-9A51-AF7E2B1C0D99}
AppName=Afterburn
AppVersion={#AppVersion}
AppVerName=Afterburn {#AppVersion}
AppPublisher=Afterburn contributors
AppCopyright=Copyright (C) Nexus Mods and NexusMods.App contributors; modifications (C) 2026 Afterburn contributors. GPL-3.0.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Afterburn installer
AppPublisherURL=https://github.com/raine423-afk/afterburn
AppSupportURL=https://github.com/raine423-afk/afterburn/issues
DefaultDirName={localappdata}\Afterburn
DefaultGroupName=Afterburn
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE.md
SetupIconFile=..\src\NexusMods.App\icon.ico
UninstallDisplayIcon={app}\Afterburn.exe
OutputDir=..\installer-out
OutputBaseFilename=Afterburn-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Afterburn.exe
AllowUNCPath=no
SetupLogging=no
UninstallDisplayName=Afterburn

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\THIRD-PARTY-PACKAGES.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\Afterburn"; Filename: "{app}\Afterburn.exe"
Name: "{group}\Uninstall Afterburn"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Afterburn"; Filename: "{app}\Afterburn.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Afterburn.exe"; Description: "{cm:LaunchProgram,Afterburn}"; Flags: nowait postinstall skipifsilent
