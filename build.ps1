<#
.SYNOPSIS
  Compila PadScreen.exe (programma Windows) in .\dist come singolo file autonomo.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ".NET SDK non trovato. Installalo con:" -ForegroundColor Yellow
    Write-Host "  winget install -e --id Microsoft.DotNet.SDK.10"
    exit 1
}

$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^10\.')) {
    Write-Host "Serve .NET SDK 10. Installato:" -ForegroundColor Yellow
    $sdks
    Write-Host "Installa con: winget install -e --id Microsoft.DotNet.SDK.10"
    exit 1
}

& dotnet publish windows/PadScreen/PadScreen.csproj `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Fatto: $(Join-Path $PSScriptRoot 'dist\PadScreen.exe')" -ForegroundColor Green
