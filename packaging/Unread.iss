#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error Pass /DPublishDir with the self-contained publish directory.
#endif
#ifndef PackageDir
  #error Pass /DPackageDir with the output directory.
#endif

[Setup]
AppId={{A43CC887-9E73-46B9-86B5-61C4BDEDA714}
AppName=Unread Reader
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppSupportURL=https://github.com/0xruth1ezz/unread-rss/issues
AppUpdatesURL=https://github.com/0xruth1ezz/unread-rss/actions
DefaultDirName={localappdata}\Programs\Unread Reader
DefaultGroupName=Unread Reader
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#PackageDir}
OutputBaseFilename=Unread-{#AppVersion}-win-x64-setup
SetupIconFile={#PublishDir}\Assets\Unread.ico
UninstallDisplayIcon={app}\Unread.App.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Unread Reader"; Filename: "{app}\Unread.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Unread Reader"; Filename: "{app}\Unread.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Unread.App.exe"; Description: "Open Unread Reader"; Flags: nowait postinstall skipifsilent

; The user's feeds, credentials and settings live outside {app}. Uninstall only removes installed files.
