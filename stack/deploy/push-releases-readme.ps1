#Requires -Version 5.1
<#
.SYNOPSIS
  Publica deploy/releases-README.md e capturas de ecrã no repo DirCompareAndSync-Releases.

.EXAMPLE
  .\deploy\push-releases-readme.ps1
#>
param(
    [string] $GitHubRepoUrl = "",
    [string] $GitHubToken = "",
    [string] $ReadmeSource = "",
    [string] $ScreenshotsDir = ""
)

$ErrorActionPreference = "Stop"

$deployRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $deployRoot

. (Join-Path $deployRoot "release-repo.ps1")

if (-not $GitHubRepoUrl) { $GitHubRepoUrl = Get-GitHubReleasesRepoUrl }
if (-not $ReadmeSource) { $ReadmeSource = Join-Path $deployRoot "releases-README.md" }
if (-not $ScreenshotsDir) { $ScreenshotsDir = Join-Path $deployRoot "screenshots" }

$GitHubToken = Resolve-GitHubTokenForDeploy -Explicit $GitHubToken
if (-not $GitHubToken) {
    throw "GITHUB_TOKEN em falta. Defina a variavel de ambiente com scope 'repo'."
}

if (-not (Test-Path $ReadmeSource)) {
    throw "Ficheiro nao encontrado: $ReadmeSource"
}

$slug = Get-GitHubRepoSlug -RepoUrl $GitHubRepoUrl
if (-not $slug) { throw "URL GitHub invalida: $GitHubRepoUrl" }

$headers = @{
    Authorization = "token $GitHubToken"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "DirCompareAndSync-deploy"
}

function Publish-GitHubRepoFile {
    param(
        [Parameter(Mandatory = $true)][string] $RepoPath,
        [Parameter(Mandatory = $true)][string] $LocalPath,
        [Parameter(Mandatory = $true)][string] $CommitMessage
    )

    $bytes = [IO.File]::ReadAllBytes($LocalPath)
    $encoded = [Convert]::ToBase64String($bytes)
    $uri = "https://api.github.com/repos/$slug/contents/$RepoPath"
    $body = @{
        message = $CommitMessage
        content = $encoded
        branch  = "main"
    }

    try {
        $existing = Invoke-RestMethod -Uri $uri -Headers $headers -ErrorAction Stop
        $body.sha = $existing.sha
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    }

    Write-Host "==> Publicar $RepoPath ..."
    Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -Body ($body | ConvertTo-Json) | Out-Null
}

function Publish-ReleaseRepoStack {
    $releasesRepoDir = Join-Path $deployRoot "releases-repo"
    $gitAttributes = Join-Path $releasesRepoDir ".gitattributes"
    if (Test-Path $gitAttributes) {
        Publish-GitHubRepoFile -RepoPath ".gitattributes" -LocalPath $gitAttributes -CommitMessage "Actualizar regras GitHub Linguist (.gitattributes)."
    }

    $stackReadme = Join-Path $releasesRepoDir "stack\README.md"
    if (Test-Path $stackReadme) {
        Publish-GitHubRepoFile -RepoPath "stack/README.md" -LocalPath $stackReadme -CommitMessage "Actualizar README da pasta stack/."
    }

    $manifestPath = Join-Path $releasesRepoDir "stack-manifest.json"
    if (-not (Test-Path $manifestPath)) {
        Write-Host "AVISO: stack-manifest.json em falta - stack/ nao actualizado."
        return
    }

    $manifest = Get-Content -Path $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $manifest.files) {
        $localPath = Join-Path $repoRoot ($entry.source -replace '/', '\')
        if (-not (Test-Path $localPath)) {
            throw "Ficheiro de stack em falta no repo de codigo: $($entry.source)"
        }

        $repoPath = ($entry.target -replace '\\', '/')
        Publish-GitHubRepoFile -RepoPath $repoPath -LocalPath $localPath -CommitMessage "Actualizar amostra de codigo $repoPath."
    }
}

if (Test-Path $ScreenshotsDir) {
    Get-ChildItem -Path $ScreenshotsDir -File -Filter "*.png" | ForEach-Object {
        $repoPath = "docs/screenshots/$($_.Name)"
        Publish-GitHubRepoFile -RepoPath $repoPath -LocalPath $_.FullName -CommitMessage "Adicionar captura de ecrã $($_.Name)."
    }
}

$docsDir = Join-Path $deployRoot "docs"
if (Test-Path $docsDir) {
    Get-ChildItem -Path $docsDir -File | ForEach-Object {
        $repoPath = "docs/$($_.Name)"
        Publish-GitHubRepoFile -RepoPath $repoPath -LocalPath $_.FullName -CommitMessage "Publicar $($_.Name) em docs/."
    }
}

Get-ChildItem -Path $deployRoot -File -Filter "google*.html" | ForEach-Object {
    Publish-GitHubRepoFile -RepoPath $_.Name -LocalPath $_.FullName -CommitMessage "Google Search Console verification file."
}

Write-Host "==> Publicar amostras de codigo (stack/) e Linguist..."
Publish-ReleaseRepoStack

$content = Get-Content -Path $ReadmeSource -Raw -Encoding UTF8
$readmeBytes = [System.Text.Encoding]::UTF8.GetBytes($content)
$readmeEncoded = [Convert]::ToBase64String($readmeBytes)
$readmeUri = "https://api.github.com/repos/$slug/contents/README.md"
$readmeBody = @{
    message = "Actualizar README (tecnologias e documentacao)."
    content = $readmeEncoded
    branch  = "main"
}

try {
    $existing = Invoke-RestMethod -Uri $readmeUri -Headers $headers -ErrorAction Stop
    $readmeBody.sha = $existing.sha
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
}

Write-Host "==> Publicar README em $GitHubRepoUrl ..."
Invoke-RestMethod -Method Put -Uri $readmeUri -Headers $headers -Body ($readmeBody | ConvertTo-Json) | Out-Null
Write-Host "Concluido: $GitHubRepoUrl"
