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
#ifndef Arch
  #define Arch "x64"
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
#if Arch == "arm64"
; Windows 11 on ARM: Petal itself runs natively, its x64 helpers (FFmpeg, ExifTool) through Windows' emulation.
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
MinVersion=10.0.22621
OutputBaseFilename=Petal-Setup-{#AppVersion}-arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=Petal-Setup-{#AppVersion}
#endif
OutputDir={#OutputDir}
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
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.Options=Options:
english.TaskStartup=Start Petal when I sign in to Windows
english.TaskMenu=Add "Convert with Petal" to File Explorer's right-click menu
english.TaskPath=Add the "petal" command to the command line (PATH)
english.StartNow=Start Petal now
arabic.Options=الخيارات:
arabic.TaskStartup=تشغيل Petal عند تسجيل الدخول إلى Windows
arabic.TaskMenu=إضافة «التحويل بـ Petal» إلى قائمة النقر الأيمن في مستكشف الملفات
arabic.TaskPath=إضافة الأمر «petal» إلى سطر الأوامر (PATH)
arabic.StartNow=تشغيل Petal الآن
turkish.Options=Seçenekler:
turkish.TaskStartup=Windows'ta oturum açtığımda Petal'ı başlat
turkish.TaskMenu=Dosya Gezgini'nin sağ tık menüsüne "Petal ile dönüştür" ekle
turkish.TaskPath="petal" komutunu komut satırına ekle (PATH)
turkish.StartNow=Petal'ı şimdi başlat
french.Options=Options :
french.TaskStartup=Démarrer Petal à l'ouverture de ma session Windows
french.TaskMenu=Ajouter « Convertir avec Petal » au menu contextuel de l'Explorateur de fichiers
french.TaskPath=Ajouter la commande « petal » à la ligne de commande (PATH)
french.StartNow=Démarrer Petal maintenant
spanish.Options=Opciones:
spanish.TaskStartup=Iniciar Petal al iniciar sesión en Windows
spanish.TaskMenu=Añadir «Convertir con Petal» al menú contextual del Explorador de archivos
spanish.TaskPath=Añadir el comando «petal» a la línea de comandos (PATH)
spanish.StartNow=Iniciar Petal ahora

[Tasks]
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:Options}"
Name: "contextmenu"; Description: "{cm:TaskMenu}"; GroupDescription: "{cm:Options}"
Name: "path"; Description: "{cm:TaskPath}"; GroupDescription: "{cm:Options}"; Flags: unchecked

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
; "--installed": Petal shows its window, so it is plain that it is installed and running.
Filename: "{app}\Petal.exe"; Parameters: "--installed"; Description: "{cm:StartNow}"; Flags: nowait postinstall skipifsilent
; Updates install silently from inside Petal: start it again afterwards.
Filename: "{app}\Petal.exe"; Parameters: "--background"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im Petal.exe /f"; Flags: runhidden; RunOnceId: "StopPetal"
; Takes File Explorer's thumbnails for design files back out (only what Petal added).
Filename: "{app}\Petal.exe"; Parameters: "--unregister-shell"; Flags: runhidden waituntilterminated; RunOnceId: "PetalShell"

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
