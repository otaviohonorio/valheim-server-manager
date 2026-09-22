<#
.SYNOPSIS
    Builds the Windows installer (ValheimServerManager-Setup-<version>.exe) with Inno Setup 6.
.DESCRIPTION
    Publishes the app into dist\ (unless -SkipPublish) and compiles build\installer.iss into
    artifacts\installer\. The version comes from Directory.Build.props unless -Version is given
    (the release workflow passes the git tag).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipPublish,
    [switch]$SkipTests,
    # Installs side by side with the real app (tests only): a different AppId and output name.
    [string]$TestAppId
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida: '$Version' (use 1.2.3)." }

$iscc = @(
    $env:ISCC,
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup' }

if (-not $SkipPublish) {
    $publishArgs = @{ }
    if ($SkipTests) { $publishArgs.SkipTests = $true }
    & (Join-Path $PSScriptRoot 'publish.ps1') @publishArgs -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'A publicação falhou.' }
}

$dist = Join-Path $repo 'dist'
if (-not (Test-Path (Join-Path $dist 'ValheimServerManager.exe'))) { throw "Não há build em $dist. Rode sem -SkipPublish." }

$defines = @("/DAppVersion=$Version")
if ($TestAppId) { $defines += "/DAppId={{$TestAppId}", "/DAppName=Valheim Server Manager Teste","/DOutputDir=..\artifacts\installer-test" }

Write-Host "== Instalador $Version ==" -ForegroundColor Cyan
& $iscc /Q @defines (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw "O Inno Setup falhou (código $LASTEXITCODE)." }

$outDir = if ($TestAppId) { 'artifacts\installer-test' } else { 'artifacts\installer' }
$setup = Join-Path $repo "$outDir\ValheimServerManager-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
# LF only: sha256sum -c rejects a CRLF line.
[IO.File]::WriteAllText("$setup.sha256", "$hash  $(Split-Path -Leaf $setup)`n")
Write-Host ("Pronto: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA-256: $hash"
