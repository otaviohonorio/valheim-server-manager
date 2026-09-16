<#
.SYNOPSIS
    Builds a self-contained release of the app and the CLI into dist\ and runs the unit tests.
.DESCRIPTION
    The output folder runs on any Windows 10 (19041+) / 11 x64 machine without installing .NET or
    the Windows App SDK. The CLI (vsm.exe) is published into a "cli" subfolder.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Output) { $Output = Join-Path $repo 'dist' }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $userDotnet) {
        $env:DOTNET_ROOT = Split-Path $userDotnet
        $env:PATH = "$($env:DOTNET_ROOT);$($env:PATH)"
    }
    else {
        throw '.NET SDK 10 não encontrado. Instale com: winget install Microsoft.DotNet.SDK.10'
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $repo
try {
    if (-not $SkipTests) {
        Write-Host '== Testes ==' -ForegroundColor Cyan
        dotnet test --project tests/ValheimServerManager.Core.Tests -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Os testes falharam.' }
    }

    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

    Write-Host '== App ==' -ForegroundColor Cyan
    dotnet publish src/ValheimServerManager.App -c $Configuration -o $Output `
        -p:PublishReadyToRun=true -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o app.' }

    Write-Host '== CLI ==' -ForegroundColor Cyan
    dotnet publish src/ValheimServerManager.Cli -c $Configuration -o (Join-Path $Output 'cli') `
        --self-contained true -p:PublishSingleFile=true -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o CLI.' }

    $size = (Get-ChildItem $Output -Recurse | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Pronto: {0} ({1:N0} MB)" -f $Output, $size) -ForegroundColor Green
}
finally {
    Pop-Location
}
