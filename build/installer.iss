; Inno Setup 6 script for Valheim Server Manager.
; Built by build/make-installer.ps1 from the self-contained output in dist\ (build/publish.ps1).
;
; Safety rules (see docs/incident-2026-09-16.md):
;  - never replace files while the manager is open (it holds the mutex below);
;  - never close the manager through Restart Manager: its session-end handler stops every server;
;  - never touch settings, worlds or backups, on install or uninstall;
;  - never install into a folder that already holds something else (saves, Steam, the game).
;
; Languages: English, Brazilian Portuguese and Spanish, picked from the Windows display language.
; Every text of ours is a [CustomMessages] entry in all three.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppId
  #define AppId "{{05809CEC-293B-4DFA-A9EB-DCBEFF66B667}"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef AppName
  #define AppName "Valheim Server Manager"
#endif
#define AppExe "ValheimServerManager.exe"
#define RunningMutex "ValheimServerManager.Running"
#define RepoUrl "https://github.com/otaviohonorio/valheim-server-manager"
#define KofiUrl "https://ko-fi.com/ottorocket"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Otávio Honório
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user install: no admin prompt, same place build/install.ps1 always used, so it upgrades it.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=no
AppendDefaultDirName=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; Restart Manager would send the manager a session-end message, which stops every server.
CloseApplications=no
RestartApplications=no

; The Windows display language picks the installer language; the list only shows when none matches.
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto

OutputDir={#OutputDir}
OutputBaseFilename=ValheimServerManager-Setup-{#AppVersion}
SetupIconFile=..\src\ValheimServerManager.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.AppComment=Manages Valheim dedicated servers safely
ptbr.AppComment=Gerencia servidores dedicados de Valheim com segurança
es.AppComment=Administra servidores dedicados de Valheim con seguridad

en.SupportProject=Support the project on Ko-fi (optional, no account needed)
ptbr.SupportProject=Apoiar o projeto no Ko-fi (opcional, sem precisar de conta)
es.SupportProject=Apoyar el proyecto en Ko-fi (opcional, sin necesidad de cuenta)

en.AppOpen=Valheim Server Manager is open.%n%nClose it from the tray icon (next to the clock): right-click > Exit… > "Exit and keep running". Servers keep running and the app picks them up again when it opens.%n%nThen click Retry.
ptbr.AppOpen=O Valheim Server Manager está aberto.%n%nFeche-o pelo ícone da bandeja (perto do relógio): clique com o botão direito > Sair… > "Sair e deixar rodando". Os servidores continuam ligados e o app volta a acompanhá-los quando abrir de novo.%n%nDepois clique em Repetir.
es.AppOpen=Valheim Server Manager está abierto.%n%nCiérralo desde el icono de la bandeja (junto al reloj): clic derecho > Salir… > "Salir y dejar funcionando". Los servidores siguen funcionando y la aplicación los vuelve a seguir cuando se abre.%n%nLuego haz clic en Reintentar.

en.AppOpenShort=Close Valheim Server Manager (tray icon > Exit… > "Exit and keep running") and run the installer again.
ptbr.AppOpenShort=Feche o Valheim Server Manager (ícone da bandeja > Sair… > "Sair e deixar rodando") e rode o instalador de novo.
es.AppOpenShort=Cierra Valheim Server Manager (icono de la bandeja > Salir… > "Salir y dejar funcionando") y vuelve a ejecutar el instalador.

en.DirIsSaves=This is a Valheim save folder. The program cannot live next to the worlds.
ptbr.DirIsSaves=Esta pasta é de saves do Valheim. O programa não pode ficar junto dos mundos.
es.DirIsSaves=Esta es una carpeta de guardados de Valheim. El programa no puede estar junto a los mundos.

en.DirIsGame=This folder belongs to Valheim or Steam. Choose a folder just for the manager.
ptbr.DirIsGame=Esta pasta é do Valheim ou da Steam. Escolha uma pasta só para o gerenciador.
es.DirIsGame=Esta carpeta es de Valheim o de Steam. Elige una carpeta solo para el administrador.

en.DirNotEmpty=This folder already has other files. Choose an empty folder (or the one of a previous install) so that updating or uninstalling never touches anything of yours.
ptbr.DirNotEmpty=Esta pasta já tem outros arquivos. Escolha uma pasta vazia (ou a de uma instalação anterior) para que atualizar ou desinstalar nunca mexa em nada seu.
es.DirNotEmpty=Esta carpeta ya tiene otros archivos. Elige una carpeta vacía (o la de una instalación anterior) para que actualizar o desinstalar nunca toque nada tuyo.

en.ReadyDataKept=Your settings, worlds and backups are not touched.
ptbr.ReadyDataKept=Suas configurações, mundos e backups não são tocados.
es.ReadyDataKept=Tu configuración, mundos y copias de seguridad no se tocan.

en.ReadySettings=Settings:
ptbr.ReadySettings=Configurações:
es.ReadySettings=Configuración:

en.ReadyServersRunning=Running servers keep running during the update.
ptbr.ReadyServersRunning=Servidores ligados continuam ligados durante a atualização.
es.ReadyServersRunning=Los servidores encendidos siguen funcionando durante la actualización.

en.ServerRunning=A Valheim server is running.%n%nOpen Valheim Server Manager and stop the servers with "Stop" (that saves the world). Then uninstall again.
ptbr.ServerRunning=Há um servidor Valheim ligado.%n%nAbra o Valheim Server Manager e desligue os servidores com "Parar" (isso salva o mundo). Depois desinstale de novo.
es.ServerRunning=Hay un servidor de Valheim encendido.%n%nAbre Valheim Server Manager y detén los servidores con "Detener" (eso guarda el mundo). Luego desinstala de nuevo.

en.UninstallKept=The program was removed. Your worlds, backups and settings were kept:%n%n%1%n(and the save and backup folders you chose for each server).
ptbr.UninstallKept=O programa foi removido. Seus mundos, backups e configurações foram mantidos:%n%n%1%n(e as pastas de saves e backups que você escolheu para cada servidor).
es.UninstallKept=El programa se eliminó. Tus mundos, copias de seguridad y configuración se conservaron:%n%n%1%n(y las carpetas de guardados y copias de seguridad que elegiste para cada servidor).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "{cm:AppComment}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "{cm:AppComment}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "{#KofiUrl}"; Description: "{cm:SupportProject}"; Flags: shellexec nowait postinstall skipifsilent unchecked

[Code]
function ProcessCount(const ExeName: String): Integer;
var
  Locator, Service, Items: Variant;
begin
  Result := 0;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + ExeName + '''');
    Result := Items.Count;
  except
    Result := 0;
  end;
end;

{ Versions before the installer do not hold the mutex, so the process name is checked too. }
function IsAppOpen: Boolean;
begin
  Result := CheckForMutexes('{#RunningMutex}') or (ProcessCount('{#AppExe}') > 0);
end;

function RunningServerCount: Integer;
begin
  Result := ProcessCount('valheim_server.exe');
end;

function WaitForAppClosed: Boolean;
begin
  Result := True;
  while IsAppOpen do
  begin
    if SuppressibleMsgBox(CustomMessage('AppOpen'), mbInformation, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function IsDirEmpty(const Dir: String): Boolean;
var
  Rec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') then
      begin
        Result := False;
        Exit;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

{ A folder is fine when it does not exist yet, is empty, or already holds this program. }
function InstallDirProblem(const Dir: String): String;
var
  Lower: String;
begin
  Result := '';
  Lower := Lowercase(AddBackslash(Dir));
  if (Pos('\irongate\valheim\', Lower) > 0) or DirExists(AddBackslash(Dir) + 'worlds_local') or
     DirExists(AddBackslash(Dir) + 'worlds') then
    Result := CustomMessage('DirIsSaves')
  else if FileExists(AddBackslash(Dir) + 'valheim_server.exe') or FileExists(AddBackslash(Dir) + 'valheim.exe') or
          (Pos('\steamapps\', Lower) > 0) then
    Result := CustomMessage('DirIsGame')
  else if DirExists(Dir) and not FileExists(AddBackslash(Dir) + '{#AppExe}') and not IsDirEmpty(Dir) then
    Result := CustomMessage('DirNotEmpty');
end;

function InitializeSetup: Boolean;
begin
  Result := WaitForAppClosed;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Problem := InstallDirProblem(WizardDirValue);
    if Problem <> '' then
    begin
      SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
      Result := False;
    end;
  end
  else if CurPageID = wpReady then
    Result := WaitForAppClosed;
end;

{ Runs in every mode (silent installs and skipped pages included): the last word on the folder. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := InstallDirProblem(ExpandConstant('{app}'));
  if (Result = '') and IsAppOpen then
    Result := CustomMessage('AppOpenShort');
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result +
    CustomMessage('ReadyDataKept') + NewLine +
    CustomMessage('ReadySettings') + ' ' + ExpandConstant('{localappdata}') + '\ValheimServerManager';
  if RunningServerCount > 0 then
    Result := Result + NewLine + NewLine + CustomMessage('ReadyServersRunning');
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForAppClosed;
  if not Result then
    Exit;

  { Without the manager, a server started hidden has no window to stop it with a save. }
  if RunningServerCount > 0 then
  begin
    SuppressibleMsgBox(CustomMessage('ServerRunning'), mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    SuppressibleMsgBox(
      FmtMessage(CustomMessage('UninstallKept'), [ExpandConstant('{localappdata}') + '\ValheimServerManager']),
      mbInformation, MB_OK, IDOK);
end;
