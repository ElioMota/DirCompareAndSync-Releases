#Requires -Version 5.1
<#
.SYNOPSIS
  Envia os assets de releases/ para GitHub Releases via API REST, SEM timeout.

.DESCRIPTION
  Alternativa robusta ao `vpk upload` para LIGACOES LENTAS. O `vpk` aborta cada
  ficheiro aos 1800s (30 min); em uploads de ~50 MB com rede lenta isso falha
  sempre. Este script usa a API REST do GitHub com Invoke-RestMethod -TimeoutSec 0
  (sem limite) e e RETOMAVEL: reutiliza a release existente e salta os assets que
  ja estao 100% enviados, por isso pode ser corrido varias vezes ate concluir.

  Envia ficheiro a ficheiro (nao usa vpk upload). Com -ParallelUploads > 1 envia
  varios assets em simultaneo para reduzir o tempo total em redes lentas.

  Pre-requisito: correr antes `.\deploy\publish-release.ps1 -Version X.Y.Z`
  (sem -UploadToGitHub) para gerar os pacotes em releases/.

.EXAMPLE
  .\deploy\upload-rest.ps1 -Version 2.0.22

.EXAMPLE
  # Ate 3 ficheiros em paralelo (recomendado em rede lenta).
  .\deploy\upload-rest.ps1 -Version 2.0.22 -ParallelUploads 3

.EXAMPLE
  # Se a rede caiu a meio, corra outra vez: salta o que ja foi enviado.
  .\deploy\upload-rest.ps1 -Version 2.0.22
#>
param(
    [string] $Version = "",
    [string] $Slug = "ElioMota/DirCompareAndSync-Releases",
    [ValidateRange(1, 6)]
    [int] $ParallelUploads = 3,
    [switch] $KeepDraft
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$relRoot = Join-Path $Root "releases"

if (-not $Version) {
    $proj = Join-Path $Root "DirCompareAndSync.Desktop\DirCompareAndSync.Desktop.csproj"
    $Version = (dotnet msbuild $proj -getProperty:Version -nologo).Trim()
    if (-not $Version) { throw "Nao foi possivel ler Version do .csproj. Use -Version X.Y.Z" }
}
$tag = "v$Version"

$token = $env:GITHUB_TOKEN
if (-not $token) { $token = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN", "User") }
if (-not $token) { $token = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN", "Machine") }
if (-not $token) { throw "GITHUB_TOKEN em falta (variavel de ambiente, scope 'repo')." }

$headers = @{
    Authorization = "token $token"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "dcs-rest-upload"
}

if (-not (Test-Path $relRoot)) { throw "Pasta releases/ nao existe. Corra publish-release.ps1 primeiro." }

function Send-ReleaseAsset {
    param(
        [string] $Slug,
        [int] $RelId,
        [hashtable] $Headers,
        [string] $Path
    )

    $name = [IO.Path]::GetFileName($Path)
    $localSize = (Get-Item $Path).Length
    $sizeMB = [math]::Round($localSize / 1MB, 1)
    $uri = "https://uploads.github.com/repos/$Slug/releases/$RelId/assets?name=$name"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-RestMethod -Method Post -Uri $uri -Headers $Headers -ContentType "application/octet-stream" -InFile $Path -TimeoutSec 0 | Out-Null
    $sw.Stop()
  return [PSCustomObject]@{
        Name = $name
        SizeMB = $sizeMB
        Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1)
    }
}

# --- Reutilizar a release existente com esta tag (ou criar draft novo). ---
$all = Invoke-RestMethod -Uri "https://api.github.com/repos/$Slug/releases?per_page=100" -Headers $headers
$rel = $all | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
if ($rel) {
    Write-Host "==> A reutilizar release existente $tag (id=$($rel.id), draft=$($rel.draft))."
} else {
    Write-Host "==> A criar release draft $tag ..."
    $bodyJson = @{ tag_name = $tag; name = "DirCompareAndSync v$Version"; draft = $true; prerelease = $false } | ConvertTo-Json
    $rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Slug/releases" -Headers $headers -ContentType "application/json" -Body $bodyJson
    Write-Host "    release draft id=$($rel.id)"
}
$relId = $rel.id

# --- Lista de assets a publicar (ficheiros essenciais + instaladores). ---
$notes = Join-Path $Root "DirCompareAndSync.Desktop\Assets\release-notes.json"
$files = @(
    (Join-Path $relRoot "DirCompareAndSync-$Version-full.nupkg"),
    (Join-Path $relRoot "DirCompareAndSync-$Version-delta.nupkg"),
    (Join-Path $relRoot "releases.win.json"),
    (Join-Path $relRoot "RELEASES"),
    (Join-Path $relRoot "DirCompareAndSync-win-Portable.zip"),
    (Join-Path $relRoot "DirCompareAndSync-win-Setup.exe"),
    (Join-Path $relRoot "DirCompareAndSync-Installer.exe"),
    $notes
)

$toUpload = New-Object System.Collections.Generic.List[string]
foreach ($path in $files) {
    if (-not (Test-Path $path)) {
        Write-Host "SKIP (nao existe): $([IO.Path]::GetFileName($path))"
        continue
    }

    $name = [IO.Path]::GetFileName($path)
    $localSize = (Get-Item $path).Length
    $existing = $rel.assets | Where-Object { $_.name -eq $name }
    if ($existing) {
        if ($existing.state -eq "uploaded" -and $existing.size -eq $localSize) {
            Write-Host "JA ENVIADO: $name ($([math]::Round($localSize/1MB,1)) MB) - saltar."
            continue
        }

        Write-Host "==> Asset $name existe mas incompleto/diferente - a substituir."
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$Slug/releases/assets/$($existing.id)" -Headers $headers
    }

    $toUpload.Add($path)
}

if ($toUpload.Count -eq 0) {
    Write-Host "Nenhum asset pendente de upload."
} elseif ($ParallelUploads -le 1 -or $toUpload.Count -eq 1) {
    Write-Host "==> Upload sequencial de $($toUpload.Count) ficheiro(s) ..."
    foreach ($path in $toUpload) {
        $name = [IO.Path]::GetFileName($path)
        $sizeMB = [math]::Round((Get-Item $path).Length / 1MB, 1)
        Write-Host "==> Upload $name ($sizeMB MB) ..."
        $result = Send-ReleaseAsset -Slug $Slug -RelId $relId -Headers $headers -Path $path
        Write-Host "    OK ($($result.Name)) em $($result.Minutes) min"
    }
} else {
    $parallel = [Math]::Min($ParallelUploads, $toUpload.Count)
    Write-Host "==> Upload paralelo: $($toUpload.Count) ficheiro(s), ate $parallel em simultaneo ..."
    $pool = [RunspaceFactory]::CreateRunspacePool(1, $parallel)
    $pool.Open()
    $handles = @()

    foreach ($path in $toUpload) {
        $name = [IO.Path]::GetFileName($path)
        $sizeMB = [math]::Round((Get-Item $path).Length / 1MB, 1)
        Write-Host "    A iniciar: $name ($sizeMB MB)"

        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $pool
        [void]$ps.AddScript({
            param($Slug, $RelId, $Headers, $Path)
            function Send-ReleaseAsset {
                param([string] $Slug, [int] $RelId, [hashtable] $Headers, [string] $Path)
                $name = [IO.Path]::GetFileName($Path)
                $localSize = (Get-Item $Path).Length
                $sizeMB = [math]::Round($localSize / 1MB, 1)
                $uri = "https://uploads.github.com/repos/$Slug/releases/$RelId/assets?name=$name"
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                Invoke-RestMethod -Method Post -Uri $uri -Headers $Headers -ContentType "application/octet-stream" -InFile $Path -TimeoutSec 0 | Out-Null
                $sw.Stop()
                return [PSCustomObject]@{ Name = $name; SizeMB = $sizeMB; Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1) }
            }
            Send-ReleaseAsset -Slug $Slug -RelId $RelId -Headers $Headers -Path $Path
        })
        [void]$ps.AddArgument($Slug)
        [void]$ps.AddArgument($relId)
        [void]$ps.AddArgument($headers)
        [void]$ps.AddArgument($path)
        $handles += [PSCustomObject]@{ PS = $ps; Async = $ps.BeginInvoke() }
    }

    foreach ($h in $handles) {
        $result = $h.PS.EndInvoke($h.Async)
        $h.PS.Dispose()
        if ($result) {
            Write-Host "    OK ($($result.Name), $($result.SizeMB) MB) em $($result.Minutes) min"
        }
    }

    $pool.Close()
    $pool.Dispose()
}

if (-not $KeepDraft) {
    Write-Host "==> A publicar release (draft=false) ..."
    Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$Slug/releases/$relId" -Headers $headers -ContentType "application/json" -Body (@{ draft = $false } | ConvertTo-Json) | Out-Null
}

Write-Host ""
Write-Host "CONCLUIDO: https://github.com/$Slug/releases/tag/$tag"
