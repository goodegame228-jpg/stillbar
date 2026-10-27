; Stillbar setup. Build with build-installer.ps1 (publishes the self-contained app first, then runs ISCC).
; Per-user install, no administrator: %LocalAppData%\Programs\Stillbar, the same place «Stillbar.exe --install» uses,
; so this setup also upgrades a copy installed that way.

#define AppVersion "0.1.0"
#define AppExe "Stillbar.exe"
; The published app. The build script passes a short path (/DAppSource=…): this repository sits deep enough that
; the published file paths pass Windows' 260-character limit, which ISCC does not support.
#ifndef AppSource
  #define AppSource "..\o\dist\app"
#endif

[Setup]
AppId=Stillbar
AppName=Stillbar
AppVersion={#AppVersion}
AppVerName=Stillbar {#AppVersion}
AppPublisher=Stillbar
AppComments=Полоска сверху, которая не мешает работать
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Stillbar Setup
DefaultDirName={localappdata}\Programs\Stillbar
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Stillbar-Setup-{#AppVersion}
SetupIconFile=..\src\Stillbar.App\Assets\stillbar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Stillbar
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 and later (the app targets 10.0.19041)
MinVersion=10.0.19041
WizardStyle=modern
; A running Stillbar is closed before its files are replaced (an update) and started again at the end
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutoStart=Запускать Stillbar вместе с Windows
en.AutoStart=Start Stillbar with Windows
ru.Extra=Дополнительно:
en.Extra=Additional options:
ru.RemoveData=Удалить также настройки, заметки, напоминания и историю буфера обмена?%n%nЕсли оставить, они вернутся при следующей установке.
en.RemoveData=Also remove settings, notes, reminders and clipboard history?%n%nIf you keep them, they come back with the next install.

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Extra}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Stillbar"; Filename: "{app}\{#AppExe}"; Comment: "Полоска сверху, которая не мешает работать"
Name: "{userdesktop}\Stillbar"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Start with Windows: the same value Stillbar's own setting writes, so the two agree
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Stillbar"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue
; An entry from an earlier «Stillbar.exe --install» would show Stillbar twice in Settings → Apps
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Stillbar"; ValueType: none; Flags: deletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Stillbar}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Closes Stillbar and removes «start with Windows» and the Claude Code hooks before the files go
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "StillbarCleanup"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\Stillbar'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Stillbar'), True, True, True);
    end;
end;
