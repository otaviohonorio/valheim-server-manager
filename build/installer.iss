; Inno Setup 6 script for Valheim Server Manager.
; Built by build/make-installer.ps1 from the self-contained output in dist\ (build/publish.ps1).
;
; Safety rules (see docs/incident-2026-09-16.md):
;  - never replace files while the manager is open (it holds the mutex below);
;  - never close the manager through Restart Manager: its session-end handler stops every server;
;  - never touch settings, worlds or backups, on install or uninstall;
;  - never install into a folder that already holds something else (saves, Steam, the game).

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
#define SponsorUrl "https://github.com/sponsors/otaviohonorio"

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
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Gerencia servidores dedicados de Valheim com segurança"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Gerencia servidores dedicados de Valheim com segurança"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{#SponsorUrl}"; Description: "Apoiar o projeto no GitHub Sponsors (opcional)"; Flags: shellexec nowait postinstall skipifsilent unchecked

[Code]
const
  AppOpenMessage =
    'O Valheim Server Manager está aberto.' + #13#10 + #13#10 +
    'Feche-o pelo ícone da bandeja (perto do relógio): clique com o botão direito > Sair... > "Sair e deixar rodando". ' +
    'Os servidores continuam ligados e o app volta a acompanhá-los quando abrir de novo.' + #13#10 + #13#10 +
    'Depois clique em Repetir.';

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
    if SuppressibleMsgBox(AppOpenMessage, mbInformation, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
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
    Result := 'Esta pasta é de saves do Valheim. O programa não pode ficar junto dos mundos.'
  else if FileExists(AddBackslash(Dir) + 'valheim_server.exe') or FileExists(AddBackslash(Dir) + 'valheim.exe') or
          (Pos('\steamapps\', Lower) > 0) then
    Result := 'Esta pasta é do Valheim ou da Steam. Escolha uma pasta só para o gerenciador.'
  else if DirExists(Dir) and not FileExists(AddBackslash(Dir) + '{#AppExe}') and not IsDirEmpty(Dir) then
    Result := 'Esta pasta já tem outros arquivos. Escolha uma pasta vazia (ou a de uma instalação anterior) ' +
              'para que atualizar ou desinstalar nunca mexa em nada seu.';
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
    Result := 'Feche o Valheim Server Manager (ícone da bandeja > Sair... > "Sair e deixar rodando") e rode o instalador de novo.';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result +
    'Suas configurações, mundos e backups não são tocados.' + NewLine +
    'Configurações: ' + ExpandConstant('{localappdata}') + '\ValheimServerManager';
  if RunningServerCount > 0 then
    Result := Result + NewLine + NewLine + 'Servidores ligados continuam ligados durante a atualização.';
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForAppClosed;
  if not Result then
    Exit;

  { Without the manager, a server started hidden has no window to stop it with a save. }
  if RunningServerCount > 0 then
  begin
    SuppressibleMsgBox(
      'Há um servidor Valheim ligado.' + #13#10 + #13#10 +
      'Abra o Valheim Server Manager e desligue os servidores com "Parar" (isso salva o mundo). ' +
      'Depois desinstale de novo.',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    SuppressibleMsgBox(
      'O programa foi removido. Seus mundos, backups e configurações foram mantidos:' + #13#10 + #13#10 +
      ExpandConstant('{localappdata}') + '\ValheimServerManager' + #13#10 +
      '(e as pastas de saves e backups que você escolheu para cada servidor).',
      mbInformation, MB_OK, IDOK);
end;
