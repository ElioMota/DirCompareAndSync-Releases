# URL do repositorio PUBLICO de releases (partilhado pelos scripts deploy).
$script:DefaultGitHubReleasesRepoUrl = "https://github.com/ElioMota/DirCompareAndSync-Releases"

function Get-GitHubReleasesRepoUrl {
    foreach ($envName in @("DCS_GITHUB_RELEASES_REPO_URL", "DCS_GITHUB_REPO_URL")) {
        $env = [Environment]::GetEnvironmentVariable($envName)
        if ($env) { return $env.Trim() }
    }
    return $script:DefaultGitHubReleasesRepoUrl
}

function Get-GitHubRepoSlug {
    param([string] $RepoUrl)
    if ($RepoUrl -match 'github\.com[:/](.+?)(\.git)?/?$') {
        return $matches[1].TrimEnd('.git')
    }
    return $null
}

function Resolve-GitHubTokenForDeploy {
    param([string] $Explicit)
    if ($Explicit) { return $Explicit }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    $user = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN", "User")
    if ($user) { return $user }
    $machine = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN", "Machine")
    if ($machine) { return $machine }
    return $null
}

function Upload-GitHubReleaseAssets {
    param(
        [Parameter(Mandatory = $true)][string] $RepoUrl,
        [Parameter(Mandatory = $true)][string] $Tag,
        [Parameter(Mandatory = $true)][string[]] $FilePaths,
        [string] $Token = ""
    )

    $token = Resolve-GitHubTokenForDeploy -Explicit $Token
    if (-not $token) {
        Write-Host "AVISO: GITHUB_TOKEN em falta - ficheiros extra nao enviados."
        return
    }

    $slug = Get-GitHubRepoSlug -RepoUrl $RepoUrl
    if (-not $slug) {
        Write-Host "AVISO: URL GitHub invalida: $RepoUrl"
        return
    }

    $headers = @{
        Authorization = "token $token"
        Accept        = "application/vnd.github+json"
        "User-Agent"  = "DirCompareAndSync-deploy"
    }

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases/tags/$Tag" -Headers $headers
    foreach ($path in $FilePaths) {
        if (-not (Test-Path $path)) { continue }
        $name = [IO.Path]::GetFileName($path)
        $uri = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$name"
        Write-Host "==> Anexar $name ..."
        Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType "application/octet-stream" -InFile $path | Out-Null
    }
}
