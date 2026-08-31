; RyuSAK community-build Windows installer (Inno Setup).
; Produces RyuSAK-<AppVersion>-Setup.exe from an electron-forge package output.
;
; Compile (CI passes the version and the packaged app dir):
;   ISCC /DAppVersion=1.7.2 installer\RyuSAK.iss
; Optional overrides:
;   /DSourceDir=..\out\RyuSAK-win32-x64   (default shown)
;   /DIconFile=..\src\assets\icon.ico     (default shown)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\RyuSAK-win32-x64"
#endif
#ifndef IconFile
  #define IconFile "..\src\assets\icon.ico"
#endif
#define AppName "RyuSAK"

[Setup]
; Keep this AppId stable across releases so installs upgrade in place.
AppId={{8D8C951E-9BC6-4C2E-9B4D-963EE45C1700}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=RyuSAK Community Build
DefaultDirName={localappdata}\Programs\RyuSAK
DefaultGroupName=RyuSAK
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=RyuSAK-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\RyuSAK.exe
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked; Check: IsStandardMode

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "portable-marker"; DestDir: "{app}"; DestName: "portable"; Flags: ignoreversion; Check: IsPortableMode

[Dirs]
Name: "{app}\electron_cache"; Check: IsPortableMode

[InstallDelete]
Type: files; Name: "{app}\portable"; Check: IsStandardMode

[Icons]
Name: "{group}\RyuSAK"; Filename: "{app}\RyuSAK.exe"; WorkingDir: "{app}"; Check: IsStandardMode
Name: "{group}\Uninstall RyuSAK"; Filename: "{uninstallexe}"; Check: IsStandardMode
Name: "{autodesktop}\RyuSAK"; Filename: "{app}\RyuSAK.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: IsStandardMode

[Run]
Filename: "{app}\RyuSAK.exe"; Description: "Launch RyuSAK"; Flags: nowait postinstall skipifsilent

[Code]
var
  InstallModePage: TInputOptionWizardPage;
  PortableMode: Boolean;

function InitializeSetup: Boolean;
begin
  PortableMode := ExpandConstant('{param:PORTABLE|0}') = '1';
  Result := True;
end;

procedure InitializeWizard;
begin
  InstallModePage := CreateInputOptionPage(
    wpWelcome,
    'Choose install mode',
    'How should RyuSAK store its settings?',
    'Standard install stores settings in your Windows user profile. Portable install stores settings next to RyuSAK in an electron_cache folder, so the folder can be moved or copied.',
    True,
    False
  );
  InstallModePage.Add('Standard install');
  InstallModePage.Add('Portable install');
  InstallModePage.SelectedValueIndex := 0;
  if PortableMode then
    InstallModePage.SelectedValueIndex := 1;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = InstallModePage.ID then
    PortableMode := InstallModePage.SelectedValueIndex = 1;
  Result := True;
end;

function IsPortableMode: Boolean;
begin
  Result := PortableMode;
end;

function IsStandardMode: Boolean;
begin
  Result := not IsPortableMode;
end;
