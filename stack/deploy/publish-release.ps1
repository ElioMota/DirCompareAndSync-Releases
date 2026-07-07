#Requires -Version 5.1
<#
.SYNOPSIS
  Publica DirCompareAndSync (GUI + CLI) e gera instalador MSI (clássico) + pacotes Velopack para Windows x64.

.EXAMPLE
  .\deploy\publish-release.ps1
  .\deploy\publish-release.ps1 -Version 1.0.5
  .\deploy\publish-release.ps1 -UploadToGitHub
  .\deploy\publish-release.ps1 -UploadToGitHub -GitHubRepoUrl "https://github.com/ElioMota/DirCompareAndSync-Releases"
  .\deploy\publish-release.ps1 -UpdateFeedUrl "https://servidor/updates/DirCompareAndSync/"
  .\deploy\publish-release.ps1 -InstLocation PerMachine
#>
param(
    [string] $Version = "",
    [string] $Runtime = "win-x64",
    [string] $PublishDir = "",
    [string] $ReleasesDir = "",
    [string] $UpdateFeedUrl = "",
    [string] $GitHubRepoUrl = "",
    [string] $GitHubToken = "",
    [switch] $UploadToGitHub,
    [ValidateSet("Either", "PerUser", "PerMachine")]
    [string] $InstLocation = "Either"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "release-repo.ps1")

$Root = Split-Path -Parent $PSScriptRoot
$DesktopProj = Join-Path $Root "DirCompareAndSync.Desktop\DirCompareAndSync.Desktop.csproj"
$AppProj = Join-Path $Root "DirCompareAndSync.App\DirCompareAndSync.App.csproj"
$Icon = Join-Path $Root "DirCompareAndSync.Desktop\Assets\app-icon.ico"
$InstWelcome = Join-Path $PSScriptRoot "install-welcome.md"
$InstConclusion = Join-Path $PSScriptRoot "install-conclusion.md"

if (-not $PublishDir) { $PublishDir = Join-Path $PSScriptRoot "publish" }
if (-not $ReleasesDir) { $ReleasesDir = Join-Path $Root "releases" }

if (-not $Version) {
    $Version = dotnet msbuild $DesktopProj -getProperty:Version -nologo
    if (-not $Version) { throw "Could not read Version from csproj" }
}

$packId = "DirCompareAndSync"
$mainExe = "DirCompareAndSync.Desktop.exe"
$toolDir = Join-Path $PSScriptRoot ".tools"

Write-Host "==> Versão: $Version | RID: $Runtime"
Write-Host "==> Publish: $PublishDir"
Write-Host "==> Releases: $ReleasesDir"

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null

Write-Host "==> dotnet publish (Desktop + CLI)…"
dotnet publish $DesktopProj -c Release -r $Runtime --self-contained true -o $PublishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $AppProj -c Release -r $Runtime --self-contained true -o $PublishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> Instalar vpk (Velopack CLI)…"
dotnet tool install vpk --version 1.2.0 --tool-path $toolDir 2>$null
if (-not (Test-Path (Join-Path $toolDir "vpk.exe"))) {
    dotnet tool update vpk --version 1.2.0 --tool-path $toolDir
}

$vpk = Join-Path $toolDir "vpk.exe"
$packArgs = @(
    "pack",
    "--packId", $packId,
    "--packVersion", $Version,
    "--packDir", $PublishDir,
    "--mainExe", $mainExe,
    "--packTitle", "DirCompareAndSync",
    "--packAuthors", "DirCompareAndSync",
    "--outputDir", $ReleasesDir,
    "--channel", "win",
    "--msi", "true",
    "--instLocation", $InstLocation,
    "--shortcuts", "Desktop,StartMenuRoot"
)

if (Test-Path $Icon) {
    $packArgs += @("--icon", $Icon)
}
if (Test-Path $InstWelcome) {
    $packArgs += @("--instWelcome", $InstWelcome)
}
if (Test-Path $InstConclusion) {
    $packArgs += @("--instConclusion", $InstConclusion)
}

Write-Host "==> vpk pack…"
& $vpk @packArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setupExe = Join-Path $ReleasesDir "DirCompareAndSync-win-Setup.exe"
$buildInno = Join-Path $PSScriptRoot "build-inno-installer.ps1"

if ((Test-Path $setupExe) -and (Test-Path $buildInno)) {
    Write-Host "==> Instalador com escolha de pasta (Inno Setup)..."
    & $buildInno -Version $Version -InstallInnoSetup
    if ($LASTEXITCODE -ne 0) {
        Write-Host "AVISO: Instalador Inno Setup nao gerado. Use Setup.exe (sem escolha de pasta) ou instale Inno Setup 6."
    }
} else {
    Write-Host "AVISO: Setup.exe em falta - instalador Inno Setup ignorado."
}

Write-Host "Concluido."
Get-ChildItem $ReleasesDir -Filter "DirCompareAndSync-Installer.exe" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  Instalador clássico (recomendado): $($_.FullName)"
}
Get-ChildItem $ReleasesDir -Filter "*.msi" | ForEach-Object {
    Write-Host "  Instalador MSI (Velopack - so para mim / todos): $($_.FullName)"
}
Get-ChildItem $ReleasesDir -Filter "*Setup*.exe" | ForEach-Object {
    Write-Host "  Instalador rápido (Setup.exe): $($_.FullName)"
}
Write-Host "  Pacotes de update: $ReleasesDir"
Write-Host ""

if ($UploadToGitHub) {
    if (-not $GitHubRepoUrl) {
        $GitHubRepoUrl = Get-GitHubReleasesRepoUrl
    }
    if (-not $GitHubToken) {
        $GitHubToken = $env:GITHUB_TOKEN
    }
    if (-not $GitHubToken) {
        throw "UploadToGitHub requer GITHUB_TOKEN (ou -GitHubToken). Crie um PAT em GitHub Settings -> Developer settings."
    }

    $tag = "v$Version"
    $releaseName = "DirCompareAndSync v$Version"

    Write-Host "==> vpk upload github ($GitHubRepoUrl)…"
    $uploadArgs = @(
        "upload", "github",
        "--outputDir", $ReleasesDir,
        "--channel", "win",
        "--repoUrl", $GitHubRepoUrl,
        "--token", $GitHubToken,
        "--publish", "true",
        "--releaseName", $releaseName,
        "--tag", $tag
    )
    & $vpk @uploadArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $repoSlug = Get-GitHubRepoSlug -RepoUrl $GitHubRepoUrl
    $releaseNotesSource = Join-Path $Root "DirCompareAndSync.Desktop\Assets\release-notes.json"
    $extraAssets = @(
        (Join-Path $ReleasesDir "DirCompareAndSync-Installer.exe"),
        $releaseNotesSource
    ) | Where-Object { Test-Path $_ }

    if ($extraAssets) {
        Upload-GitHubReleaseAssets -RepoUrl $GitHubRepoUrl -Tag $tag -FilePaths $extraAssets -Token $GitHubToken
    } elseif (-not (Test-Path (Join-Path $ReleasesDir "DirCompareAndSync-Installer.exe"))) {
        Write-Host "AVISO: DirCompareAndSync-Installer.exe nao encontrado (Inno Setup). Utilizadores so tem Setup.exe sem escolha de pasta."
    }

    $legacyExtra = @(
        (Join-Path $ReleasesDir "DirCompareAndSync-win-Setup.exe"),
        (Join-Path $ReleasesDir "DirCompareAndSync-win.msi")
    ) | Where-Object { Test-Path $_ }

    if ($legacyExtra -and (Get-Command gh -ErrorAction SilentlyContinue) -and $repoSlug) {
        Write-Host "==> Anexar instaladores Velopack (gh)..."
        foreach ($asset in $legacyExtra) {
            gh release upload $tag $asset --repo $repoSlug --clobber
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    } elseif ($legacyExtra) {
        Write-Host "AVISO: gh CLI nao encontrado. Anexe manualmente Setup.exe/MSI se necessario."
    }

    Write-Host ""
    Write-Host "Release publicada: $GitHubRepoUrl/releases/tag/$tag"
    Write-Host "A app (instalada via Velopack) usa Ajuda -> Verificar actualizacoes..."
    Write-Host ""
}

Write-Host "Distribua DirCompareAndSync-Installer.exe para instalacao com escolha de pasta."
Write-Host "O .msi Velopack nao permite escolher pasta - so o ambito (utilizador vs todos)."
Write-Host ""
Write-Host "Teste local de updates (PowerShell):"
$releasesUri = ($ReleasesDir -replace '\\', '/')
if ($releasesUri -match '^([A-Za-z]):') {
    $releasesUri = "/$($Matches[1]):$($releasesUri.Substring(2))"
}
Write-Host "  `$env:DCS_UPDATE_FEED_URL = 'file:///$releasesUri/'"
Write-Host ""
if ($UpdateFeedUrl) {
    Write-Host "Feed sugerido para producao: $UpdateFeedUrl"
    Write-Host "Copie o conteudo de '$ReleasesDir' para esse URL e defina DCS_UPDATE_FEED_URL ou AppDeployInfo.UpdateFeedUrl"
}
Write-Host ""
Write-Host "Nota: para voltar a gerar a mesma versao, apague os ficheiros em '$ReleasesDir' antes de correr o script."
