#define MyAppName "Bastion"
#ifndef MyAppVersion
#define MyAppVersion "2.9.21"
#endif
#ifndef PublishDir
#define PublishDir "..\release\Bastion-v2.9.21-win-x64"
#endif
#ifndef OutputDir
#define OutputDir "..\release\installer-exe"
#endif

[Setup]
AppId={{B9E9E5BD-1986-48BE-9F0D-A7B6E696BD77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Hazza-uxdev
DefaultDirName={autopf}\Bastion
DefaultGroupName=Bastion
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=BastionSetup-v{#MyAppVersion}-win-x64
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#PublishDir}\Bastion.ico
UninstallDisplayIcon={app}\Bastion.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Bastion"; Filename: "{app}\Bastion.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Bastion"; Filename: "{app}\Bastion.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Bastion.exe"; Description: "Launch Bastion"; Flags: nowait postinstall skipifsilent
