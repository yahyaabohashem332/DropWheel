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
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "startup"; Description: "Start Petal when I sign in to Windows"; GroupDescription: "Options:"
Name: "contextmenu"; Description: "Add ""Convert with Petal"" to File Explorer's right-click menu"; GroupDescription: "Options:"
Name: "path"; Description: "Add the ""petal"" command to the command line (PATH)"; GroupDescription: "Options:"; Flags: unchecked

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
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: path; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\Petal.exe"; Description: "Start Petal now"; Flags: nowait postinstall skipifsilent
; Updates install silently from inside Petal: start it again afterwards.
Filename: "{app}\Petal.exe"; Parameters: "--background"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im Petal.exe /f"; Flags: runhidden; RunOnceId: "StopPetal"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Petal"

[Code]
function NeedsAddPath(Dir: string): Boolean;
var
  Current: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Current) then
    Result := True
  else
    Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Current) + ';') = 0;
end;

{ Take Petal's folder back out of the user's PATH. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Current, Dir: string;
  P: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Current) then
    exit;
  Dir := ExpandConstant('{app}');
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Current) + ';');
  if P = 0 then
    exit;
  Delete(Current, P, Length(Dir) + 1);
  if (Length(Current) > 0) and (Current[Length(Current)] = ';') then
    Delete(Current, Length(Current), 1);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Current);
end;
