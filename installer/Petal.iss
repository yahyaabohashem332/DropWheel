; Petal installer (Inno Setup 6). Built by tools\scripts\build.ps1.
; Per-user install: no admin rights needed, like Petal's own settings and context-menu entry.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Petal"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6F1E3C2A-8B7D-4E59-9A41-5C0D2B7E9F13}
AppName=Petal
AppVersion={#AppVersion}
AppVerName=Petal {#AppVersion}
AppPublisher=Petal
DefaultDirName={localappdata}\Programs\Petal
DefaultGroupName=Petal
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Petal-Setup-{#AppVersion}
SetupIconFile=..\src\Petal.App\Assets\Petal.ico
UninstallDisplayIcon={app}\Petal.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "startup"; Description: "Start Petal when I sign in to Windows"; GroupDescription: "Options:"
Name: "contextmenu"; Description: "Add ""Convert with Petal"" to File Explorer's right-click menu"; GroupDescription: "Options:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Petal"; Filename: "{app}\Petal.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Petal"; ValueData: """{app}\Petal.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\*\shell\Petal"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Convert with Petal"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Petal"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Petal.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\Petal\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Petal.exe"" --wheel ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Petal"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Petal actions"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Petal"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Petal.exe"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Petal\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Petal.exe"" --wheel ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\Petal.exe"; Description: "Start Petal now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im Petal.exe /f"; Flags: runhidden; RunOnceId: "StopPetal"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Petal"
