#Requires -Version 5.1
<#
.SYNOPSIS
  Compila DirCompareAndSync-Installer.exe (Inno Setup — escolha de pasta).

.EXAMPLE
  .\deploy\build-inno-installer.ps1
  .\deploy\build-inno-installer.ps1 -InstallInnoSetup
#>
param(
    [string] $Version = "",
    [string] $ReleasesDir = "",
    [switch] $InstallInnoSetup
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$DesktopProj = Join-Path $Root "DirCompareAndSync.Desktop\DirCompareAndSync.Desktop.csproj"
$issFile = Join-Path $PSScriptRoot "DirCompareAndSync.iss"

if (-not $ReleasesDir) { $ReleasesDir = Join-Path $Root "releases" }
if (-not $Version) {
    $Version = dotnet msbuild $DesktopProj -getProperty:Version -nologo
    if (-not $Version) { throw "Nao foi possivel ler Version do .csproj" }
}

$setupExe = Join-Path $ReleasesDir "DirCompareAndSync-win-Setup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Em falta: $setupExe`nCorra primeiro: .\deploy\publish-release.ps1"
}

function Find-InnoSetupCompiler {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
    )
    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$iscc = Find-InnoSetupCompiler
if (-not $iscc -and $InstallInnoSetup) {
    Write-Host "==> Instalar Inno Setup 6 (winget)..."
    winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
    $iscc = Find-InnoSetupCompiler
}

if (-not $iscc) {
    throw @"
Inno Setup 6 nao encontrado.

Instale manualmente: https://jrsoftware.org/isdl.php
Ou: winget install JRSoftware.InnoSetup

Depois: .\deploy\build-inno-installer.ps1
"@
}

Write-Host "==> Compilar instalador (escolha de pasta)..."
& $iscc $issFile "/DMyAppVersion=$Version" "/DSetupSource=$setupExe"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$installer = Join-Path $ReleasesDir "DirCompareAndSync-Installer.exe"
if (-not (Test-Path $installer)) {
    throw "Instalador nao gerado: $installer"
}

Write-Host ""
Write-Host "Instalador criado: $installer"
Write-Host "Distribua este ficheiro (nao o Setup.exe) para permitir escolha de pasta."
