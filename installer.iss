#define AppName "Activity Agent"
#define AppVersion "1.1.0"
#define AppPublisher ""
#define AppExeName "ActivityAgent.exe"

[Setup]
AppId={{9E2D5C58-7A65-4C16-A0B6-4F8E998C1D22}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\ActivityAgent
DefaultGroupName=Activity Agent
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=output
OutputBaseFilename=ActivityAgent-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Activity Agent"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\app.ico"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 Activity Agent"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'ActivityAgent');
end;
