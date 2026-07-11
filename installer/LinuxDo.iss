; LinuxDo Windows installer (Inno Setup 6)
; Built by Package.ps1 — defines:
;   MyAppBuildId, MyAppSourceDir, MyAppOutputDir, MyAppOutputBase

#ifndef MyAppBuildId
  #define MyAppBuildId "dev"
#endif
#ifndef MyAppSourceDir
  #define MyAppSourceDir "..\dist\LinuxDo-latest"
#endif
#ifndef MyAppOutputDir
  #define MyAppOutputDir "..\dist"
#endif
#ifndef MyAppOutputBase
  #define MyAppOutputBase "LinuxDo_Setup_" + MyAppBuildId + "_x64"
#endif

#define MyAppName "LinuxDo"
#define MyAppPublisher "LinuxDo Community (unofficial)"
#define MyAppURL "https://github.com/ct-jyjntc/linuxdo_for_win"
#define MyAppExeName "LinuxDo.exe"

[Setup]
AppId={{F9B5A1A2-51F9-4CE4-8100-87B6CC5C04D1}
AppName={#MyAppName}
; Display build id in UI; VersionInfo* must be dotted numeric for PE resources
AppVersion={#MyAppBuildId}
AppVerName={#MyAppName} {#MyAppBuildId}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#MyAppOutputDir}
OutputBaseFilename={#MyAppOutputBase}
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
; Close running app before upgrade
CloseApplications=yes
RestartApplications=no
; PE version resources require N.N.N.N (build id is shown via AppVerName)
VersionInfoVersion=1.0.0.0
VersionInfoProductVersion=1.0.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoTextVersion={#MyAppBuildId}
VersionInfoProductTextVersion={#MyAppBuildId}

[Languages]
; Official Chinese pack may be absent on minimal installs — English is enough for setup UI.
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Remove leftover files from previous incomplete installs (prevents mixed DLL sets / crashes)
Type: filesandordirs; Name: "{app}\*"

[Files]
; Entire published app folder (must include Assets\, Bootstrap DLLs, etc.)
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
