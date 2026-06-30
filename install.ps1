<#
.SYNOPSIS
    Agentweaver one-liner installer for Windows.

.DESCRIPTION
    Sets up the Agentweaver local dev environment or deploys to AKS.

    Local dev (default):
      Checks prereqs (.NET 10, Node 20+, git), installs web npm deps,
      restores .NET packages, then launches start-dev.ps1.

    AKS deploy (--Aks):
      Delegates to install.sh via WSL2 (bash must be available).

.PARAMETER Local
    Set up local dev environment (default when no mode is specified).

.PARAMETER Aks
    Deploy to Azure Kubernetes Service via WSL2/bash.

.PARAMETER SkipPostgres
    (AKS) Skip Postgres provisioning (17-provision-postgres.sh).

.PARAMETER SkipOauthKey
    (AKS) Skip OAuth signing key provisioning (16-provision-oauth-signing-key.sh).

.PARAMETER ImageTag
    (AKS) Use this image tag instead of the short git SHA. Re-run with a new tag to
    build, push, and redeploy (this is the update/redeploy path). Never use 'latest' —
    always pin to a specific SHA for reproducible deployments.

.PARAMETER SkipBuild
    (Local) Skip dotnet build in start-dev.ps1.

.PARAMETER NoBrowser
    (Local) Do not open the browser after startup.

.EXAMPLE
    # Local dev — default
    irm https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1 | iex

    # Local dev with flags
    .\install.ps1
    .\install.ps1 -SkipBuild

    # AKS deploy
    .\install.ps1 -Aks

    # AKS deploy, skip Postgres provisioning
    .\install.ps1 -Aks -SkipPostgres

    # AKS redeploy with a specific image tag
    .\install.ps1 -Aks -ImageTag abc1234
#>
param(
    [switch] $Local,
    [switch] $Aks,
    [switch] $SkipPostgres,
    [switch] $SkipOauthKey,
    [string] $ImageTag = "",
    [switch] $SkipBuild,
    [switch] $NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Colour helpers ─────────────────────────────────────────────────────────────
function Write-Info    { param([string]$Msg) Write-Host "  [info]  $Msg" -ForegroundColor Cyan }
function Write-Ok      { param([string]$Msg) Write-Host "  [ok]    $Msg" -ForegroundColor Green }
function Write-Warn    { param([string]$Msg) Write-Host "  [warn]  $Msg" -ForegroundColor Yellow }
function Write-Fail    { param([string]$Msg) Write-Host "  [error] $Msg" -ForegroundColor Red }
function Fail          { param([string]$Msg) Write-Fail $Msg; exit 1 }

# ── Determine mode ─────────────────────────────────────────────────────────────
if ($Aks -and $Local) { Fail "-Aks and -Local are mutually exclusive." }
$Mode = if ($Aks) { "aks" } else { "local" }

# ── Locate repo root ───────────────────────────────────────────────────────────
# Override for forks: set $env:AGENTWEAVER_REPO_URL to point at your fork.
$RepoUrl = if ($env:AGENTWEAVER_REPO_URL) { $env:AGENTWEAVER_REPO_URL } `
           else { "https://github.com/sabbour/agentweaver.git" }

$RepoRoot = $PSScriptRoot
if (-not $RepoRoot) {
    # Piped via iex — use the current directory
    $RepoRoot = (Get-Location).Path
}
if (-not (Test-Path (Join-Path $RepoRoot "agentweaver.sln")) -and
    -not (Test-Path (Join-Path $RepoRoot "global.json"))) {
    # ── Bootstrap: not inside a checkout — clone first ───────────────────────
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "git is not installed and no checkout was found. Install git from https://git-scm.com/ then re-run."
    }
    $CloneDir = Join-Path $HOME "agentweaver"
    if ((Test-Path (Join-Path $CloneDir "agentweaver.sln")) -or
        (Test-Path (Join-Path $CloneDir "global.json"))) {
        Write-Info "Found existing checkout at $CloneDir — using it."
    } else {
        Write-Info "No checkout found. Cloning $RepoUrl → $CloneDir"
        git clone --depth 1 $RepoUrl $CloneDir
        if ($LASTEXITCODE -ne 0) { Fail "git clone failed. Check your network and the repo URL." }
    }
    $RepoRoot = $CloneDir
    # Re-invoke the cloned copy with the same parameters
    & (Join-Path $CloneDir "install.ps1") @PSBoundParameters
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "  Agentweaver Installer  (mode: $Mode)" -ForegroundColor White
Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# LOCAL DEV MODE
# ══════════════════════════════════════════════════════════════════════════════
function Install-Local {
    Write-Host "── Checking prerequisites ──" -ForegroundColor White

    # git
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "git is not installed. Install git from https://git-scm.com/ then re-run."
    }
    $gitVer = (git --version) -replace 'git version ', ''
    Write-Ok "git $gitVer"

    # .NET 10
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail ".NET SDK is not installed. Install .NET 10 SDK from https://dot.net/download then re-run."
    }
    $dotnetVer = (dotnet --version 2>$null) ?? "unknown"
    $dotnetMajor = [int]($dotnetVer.Split('.')[0])
    if ($dotnetMajor -lt 10) {
        Fail ".NET 10 SDK is required (found $dotnetVer). Install from https://dot.net/download"
    }
    Write-Ok "dotnet $dotnetVer"

    # Node / npm
    if (-not (Get-Command node -ErrorAction SilentlyContinue) -or
        -not (Get-Command npm  -ErrorAction SilentlyContinue)) {
        Fail "Node.js (>=20.19) and npm are required. Install from https://nodejs.org/"
    }
    $nodeVer  = (node --version).TrimStart('v')
    $nodeMajor = [int]($nodeVer.Split('.')[0])
    if ($nodeMajor -lt 20) {
        Fail "Node.js 20.19+ or 22.12+ is required (found $nodeVer). Install from https://nodejs.org/"
    }
    Write-Ok "node v$nodeVer"

    Write-Host ""
    Write-Host "── Installing web dependencies ──" -ForegroundColor White
    $webDir = Join-Path $RepoRoot "apps\web"
    npm --prefix $webDir install
    if ($LASTEXITCODE -ne 0) { Fail "npm install failed." }
    Write-Ok "Web dependencies installed."

    Write-Host ""
    Write-Host "── Restoring .NET packages ──" -ForegroundColor White
    dotnet restore (Join-Path $RepoRoot "agentweaver.sln") -v q --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed." }
    Write-Ok ".NET packages restored."

    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  LOCAL DEV READY" -ForegroundColor White
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Configure the GitHub OAuth client secret (once):"
    Write-Host "    cd apps\Agentweaver.Api"
    Write-Host '    dotnet user-secrets set "Auth:GitHub:ClientSecret" "<your-oauth-app-client-secret>"'
    Write-Host ""
    Write-Host "  Launching start-dev.ps1 ..." -ForegroundColor DarkGray
    Write-Host ""

    $startDevScript = Join-Path $RepoRoot "start-dev.ps1"
    $startDevArgs   = @()
    if ($SkipBuild)  { $startDevArgs += "-SkipBuild" }
    if ($NoBrowser)  { $startDevArgs += "-NoBrowser" }

    & $startDevScript @startDevArgs
}

# ══════════════════════════════════════════════════════════════════════════════
# AKS DEPLOY MODE  (delegates to install.sh via WSL2)
# ══════════════════════════════════════════════════════════════════════════════
function Install-Aks {
    $missingAksEnv = @()
    if (-not $env:GITHUB_CLIENT_ID) { $missingAksEnv += "GITHUB_CLIENT_ID" }
    if (-not $env:GITHUB_CLIENT_SECRET) { $missingAksEnv += "GITHUB_CLIENT_SECRET" }
    if ($missingAksEnv.Count -gt 0) {
        Fail "Set required GitHub OAuth environment variables before AKS install: $($missingAksEnv -join ', ')"
    }

    # Verify WSL2 / bash is available
    if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
        Fail "WSL2 is required for AKS deployment. Enable WSL2 and install a Linux distro first."
    }

    $installSh = Join-Path $RepoRoot "install.sh"
    if (-not (Test-Path $installSh)) {
        Fail "install.sh not found at $installSh. Ensure both install scripts are present in the repo root."
    }

    # Build bash argument list
    $bashArgs = @("--aks")
    if ($SkipPostgres)  { $bashArgs += "--skip-postgres" }
    if ($SkipOauthKey)  { $bashArgs += "--skip-oauth-key" }
    if ($ImageTag)      { $bashArgs += "--image-tag"; $bashArgs += $ImageTag }
    $bashArgsStr = $bashArgs -join " "

    # Convert Windows path to WSL path.
    if ($RepoRoot -match '^([A-Za-z]):\\(.*)$') {
        $drive = $Matches[1].ToLowerInvariant()
        $rest = $Matches[2] -replace '\\', '/'
        $wslRepoRoot = "/mnt/$drive/$rest"
    } else {
        Fail "Cannot convert repo path '$RepoRoot' to a WSL path. Use a drive-letter path such as C:\path\agentweaver."
    }

    Write-Info "Delegating AKS deployment to install.sh via WSL2..."
    Write-Info "  WSL repo root: $wslRepoRoot"
    Write-Host ""

    $wslEnvNames = @("GITHUB_CLIENT_ID", "GITHUB_CLIENT_SECRET", "AGENTWEAVER_REPO_URL")
    $existingWslEnv = if ($env:WSLENV) { $env:WSLENV.Split(':') } else { @() }
    $env:WSLENV = ($existingWslEnv + $wslEnvNames | Select-Object -Unique) -join ':'

    wsl --exec bash -c "cd '$wslRepoRoot' && bash install.sh $bashArgsStr"
    if ($LASTEXITCODE -ne 0) { Fail "AKS installation failed. See output above." }
}

switch ($Mode) {
    "local" { Install-Local }
    "aks"   { Install-Aks }
}
