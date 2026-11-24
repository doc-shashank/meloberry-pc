#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{9E8B7A62-4C2E-4D1A-9F31-7A2C8E4B5101}}
AppName=Meloberry PC
AppVersion={#MyAppVersion}
AppPublisher=Meloberry
AppPublisherURL=https://github.com/doc-shashank/meloberry-pc
AppSupportURL=https://github.com/doc-shashank/meloberry-pc/releases
DefaultDirName={localappdata}\Programs\Meloberry PC
DefaultGroupName=Meloberry PC
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=MeloberryPcSingleInstanceMutex
CloseApplications=force
RestartApplications=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=..\app
OutputBaseFilename=mby-pc-setup-{#MyAppVersion}
UninstallDisplayIcon={app}\mby-pc.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\app\mby-pc-{#MyAppVersion}.exe"; DestDir: "{app}"; DestName: "mby-pc.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\Meloberry PC"; Filename: "{app}\mby-pc.exe"
Name: "{group}\Uninstall Meloberry PC"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Meloberry PC"; Filename: "{app}\mby-pc.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\mby-pc.exe"; Description: "{cm:LaunchProgram,Meloberry PC}"; Flags: nowait postinstall skipifsilent
