<#
.SYNOPSIS
    Publishes (optional) and installs Valheim Server Manager for the current user, with shortcuts.
.DESCRIPTION
    Copies dist\ to %LOCALAPPDATA%\Programs\Valheim Server Manager (or -InstallDir) and creates
    Start menu and Desktop shortcuts. Settings and backups are never touched. Refuses to run while
    the app is open, so a running manager is never overwritten mid-flight.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\install.ps1 -Publish
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Valheim Server Manager'),
    [switch]$Publish,
    [switch]$NoShortcuts
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $repo 'dist'

if (Get-Process -Name 'ValheimServerManager' -ErrorAction SilentlyContinue) {
    throw 'Feche o Valheim Server Manager antes de instalar (clique com o botão direito no ícone da bandeja > Sair). Os servidores podem continuar rodando.'
}

if ($Publish -or -not (Test-Path (Join-Path $dist 'ValheimServerManager.exe'))) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'A publicação falhou.' }
}

# robocopy /MIR deletes whatever the folder had that dist\ does not: only an empty folder or a
# previous install may be mirrored.
if ((Test-Path $InstallDir) -and -not (Test-Path (Join-Path $InstallDir 'ValheimServerManager.exe')) -and
    (Get-ChildItem -Force $InstallDir | Select-Object -First 1)) {
    throw "A pasta $InstallDir já tem outros arquivos e eles seriam apagados. Escolha uma pasta vazia ou a de uma instalação anterior."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
robocopy $dist $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar os arquivos (robocopy $LASTEXITCODE)." }
$global:LASTEXITCODE = 0

$exe = Join-Path $InstallDir 'ValheimServerManager.exe'
if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $targets = @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Valheim Server Manager.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Valheim Server Manager.lnk')
    )
    foreach ($lnk in $targets) {
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath = $exe
        $s.WorkingDirectory = $InstallDir
        $s.IconLocation = "$exe,0"
        $s.Description = 'Gerencia servidores dedicados de Valheim com segurança'
        $s.Save()
    }
}

Write-Host "Instalado em $InstallDir" -ForegroundColor Green
Write-Host "CLI: $(Join-Path $InstallDir 'cli\vsm.exe')"
