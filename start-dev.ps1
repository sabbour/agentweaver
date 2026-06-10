<#
.SYNOPSIS
    Start the Scaffolders development environment.

.DESCRIPTION
    Starts two processes:
      - Scaffolder.Api  — runs inside WSL2 using the Linux .NET 10 runtime
                          (picks up the bwrap sandbox executor automatically)
      - Web UI          — runs on Windows via Vite dev server

    The API listens on http://localhost:5000 (CORS allows localhost:5173).
    The Web UI listens on http://localhost:5173.

.PARAMETER SkipBuild
    Skip `dotnet build` before launching the API.

.PARAMETER NoBrowser
    Do not open the browser after both processes are ready.

.EXAMPLE
    .\start-dev.ps1
    .\start-dev.ps1 -SkipBuild
#>
param(
    [switch] $SkipBuild,
    [switch] $NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = $PSScriptRoot
$apiProject = "apps/Scaffolder.Api"
$webDir     = Join-Path $repoRoot "apps\web"
$apiUrl     = "http://localhost:5000"
$webUrl     = "http://localhost:5173"

# Convert Windows repo root to WSL path (C:\... -> /mnt/c/...)
$wslRepoRoot = ($repoRoot -replace '^([A-Za-z]):\\', { "/mnt/$($_.Groups[1].Value.ToLower())/" }) -replace '\\', '/'

Write-Host ""
Write-Host "  Scaffolders Dev" -ForegroundColor Cyan
Write-Host "  API  $apiUrl  (WSL2 / Linux .NET)" -ForegroundColor DarkCyan
Write-Host "  Web  $webUrl  (Windows / Vite)" -ForegroundColor DarkCyan
Write-Host ""

# ── 1. Kill any stale API processes in WSL ────────────────────────────────────
# The MAF FileSystemJsonCheckpointStore holds an exclusive lock on its directory.
# If a previous instance is still running (e.g. from an earlier dev session),
# the new one will crash immediately with "store already in use".
Write-Host "Stopping any existing API processes in WSL..." -ForegroundColor DarkGray
wsl --exec bash -c "pkill -f 'dotnet.*Scaffolder.Api' 2>/dev/null || true; sleep 1"

# ── 2. Optional build (Windows) so WSL picks up the latest IL ────────────────
if (-not $SkipBuild) {
    Write-Host "Building API..." -ForegroundColor Yellow
    Push-Location $repoRoot
    dotnet build $apiProject -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    Pop-Location
    Write-Host "Build OK" -ForegroundColor Green
    Write-Host ""
}

# ── 3. Write a bash launcher script to a temp file ───────────────────────────
#
# Windows Terminal parses its argument string and splits on semicolons, so
# passing a compound bash -c "cmd1; cmd2; cmd3" via Start-Process wt causes
# each semicolon-separated fragment to become its own WT tab/pane.
# Writing to a .sh file sidesteps all quoting/splitting issues.
#
$bashScript = @"
#!/bin/bash
cd '$wslRepoRoot'
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project $apiProject --configuration Release --urls $apiUrl --no-build
echo ""
echo "API process exited (code: \$?). Press Enter to close."
read
"@

$tmpSh       = Join-Path $env:TEMP "scaffolder-start-api.sh"
$wslTmpSh    = ($tmpSh -replace '^([A-Za-z]):\\', { "/mnt/$($_.Groups[1].Value.ToLower())/" }) -replace '\\', '/'
Set-Content -Path $tmpSh -Value $bashScript -Encoding UTF8 -NoNewline

# ── 4. Start API inside WSL2 ─────────────────────────────────────────────────
Write-Host "Starting API in WSL2..." -ForegroundColor Yellow

$wtAvailable = $null -ne (Get-Command wt -ErrorAction SilentlyContinue)
if ($wtAvailable) {
    # wt new-tab -- wsl bash /path/to/script.sh
    # The '--' stops wt from interpreting further args as its own commands.
    Start-Process wt -ArgumentList @("new-tab", "--", "wsl", "bash", $wslTmpSh)
} else {
    Start-Process wsl -ArgumentList @("bash", $wslTmpSh)
}

# ── 5. Start Web UI on Windows ────────────────────────────────────────────────
Write-Host "Starting Web UI (Vite)..." -ForegroundColor Yellow

$npmStart = {
    param($dir)
    Set-Location $dir
    npm run dev
}

$webJob = Start-Job -ScriptBlock $npmStart -ArgumentList $webDir

# ── 6. Wait for API readiness ─────────────────────────────────────────────────
Write-Host ""
Write-Host "Waiting for API to be ready at $apiUrl ..." -ForegroundColor Yellow

$maxWait  = 60
$elapsed  = 0
$apiReady = $false

while ($elapsed -lt $maxWait) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    try {
        $resp = Invoke-WebRequest -Uri "$apiUrl/" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $apiReady = $true; break }
    } catch {
        Write-Host "  ... ($elapsed s)" -ForegroundColor DarkGray
    }
}

Write-Host ""
if ($apiReady) {
    Write-Host "  API is ready" -ForegroundColor Green
} else {
    Write-Host "  API did not respond within $maxWait s — check the WSL window for errors" -ForegroundColor Red
}

# ── 7. Wait for Vite ─────────────────────────────────────────────────────────
Write-Host "Waiting for Vite..." -ForegroundColor Yellow
$viteReady = $false
$viteWait  = 0
while ($viteWait -lt 20) {
    Start-Sleep -Seconds 1
    $viteWait++
    $log = Receive-Job $webJob 2>&1
    if ($log -match "localhost:5173") { $viteReady = $true; break }
}

if ($viteReady) {
    Write-Host "  Web UI is ready" -ForegroundColor Green
} else {
    Write-Host "  Vite starting (may still be installing dependencies)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  API   $apiUrl" -ForegroundColor White
Write-Host "  Web   $webUrl" -ForegroundColor White
Write-Host "  Key   dev-local-key" -ForegroundColor DarkGray
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

if (-not $NoBrowser -and $viteReady) {
    Start-Process $webUrl
}

Write-Host "Press Ctrl+C to stop the Web UI job." -ForegroundColor DarkGray
Write-Host "(Close the WSL window to stop the API.)" -ForegroundColor DarkGray
Write-Host ""

try {
    while ($true) {
        $out = Receive-Job $webJob 2>&1
        if ($out) { $out | ForEach-Object { Write-Host "  [vite] $_" -ForegroundColor DarkCyan } }
        Start-Sleep -Milliseconds 500
    }
} finally {
    Stop-Job  $webJob -ErrorAction SilentlyContinue
    Remove-Job $webJob -ErrorAction SilentlyContinue
    Write-Host "Web UI stopped." -ForegroundColor Yellow
}


Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = $PSScriptRoot
$apiProject = "apps/Scaffolder.Api"
$webDir     = Join-Path $repoRoot "apps\web"
$apiUrl     = "http://localhost:5000"
$webUrl     = "http://localhost:5173"

# Convert Windows repo root to WSL path (C:\... -> /mnt/c/...)
$wslRepoRoot = ($repoRoot -replace '^([A-Za-z]):\\', { "/mnt/$($_.Groups[1].Value.ToLower())/" }) -replace '\\', '/'

Write-Host ""
Write-Host "  Scaffolders Dev" -ForegroundColor Cyan
Write-Host "  API  $apiUrl  (WSL2 / Linux .NET)" -ForegroundColor DarkCyan
Write-Host "  Web  $webUrl  (Windows / Vite)" -ForegroundColor DarkCyan
Write-Host ""

# ── 1. Optional build (Windows) so WSL picks up the latest IL ────────────────
if (-not $SkipBuild) {
    Write-Host "Building API..." -ForegroundColor Yellow
    Push-Location $repoRoot
    dotnet build $apiProject -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    Pop-Location
    Write-Host "Build OK" -ForegroundColor Green
    Write-Host ""
}

# ── 2. Start API inside WSL2 ─────────────────────────────────────────────────
#
# We launch a new Windows Terminal tab (if available) or a plain PowerShell
# window, running `wsl` which drops into WSL2 and starts the API there.
# ASPNETCORE_ENVIRONMENT=Development loads appsettings.Development.json
# (dev API key, CORS for localhost:5173, debug logging).
#
$wslCmd = "cd '$wslRepoRoot' && ASPNETCORE_ENVIRONMENT=Development dotnet run --project $apiProject --configuration Release --urls $apiUrl --no-build 2>&1; echo 'API process exited'; read -p 'Press Enter to close'"
$wslArgs = @("bash", "-l", "-c", $wslCmd)

Write-Host "Starting API in WSL2..." -ForegroundColor Yellow

# Try Windows Terminal first, fall back to a plain conhost window
$wtAvailable = $null -ne (Get-Command wt -ErrorAction SilentlyContinue)
if ($wtAvailable) {
    Start-Process wt -ArgumentList "wsl $($wslArgs -join ' ')"
} else {
    Start-Process wsl -ArgumentList $wslArgs
}

# ── 3. Start Web UI on Windows ────────────────────────────────────────────────
Write-Host "Starting Web UI (Vite)..." -ForegroundColor Yellow

$npmStart = {
    param($dir)
    Set-Location $dir
    npm run dev
}

$webJob = Start-Job -ScriptBlock $npmStart -ArgumentList $webDir

# ── 4. Wait for API readiness ─────────────────────────────────────────────────
Write-Host ""
Write-Host "Waiting for API to be ready at $apiUrl ..." -ForegroundColor Yellow

$maxWait   = 60   # seconds
$elapsed   = 0
$apiReady  = $false

while ($elapsed -lt $maxWait) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    try {
        $resp = Invoke-WebRequest -Uri "$apiUrl/" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -eq 200) {
            $apiReady = $true
            break
        }
    } catch {
        # Not yet ready — keep waiting
        Write-Host "  ... ($elapsed s)" -ForegroundColor DarkGray
    }
}

Write-Host ""
if ($apiReady) {
    Write-Host "  API is ready" -ForegroundColor Green
} else {
    Write-Host "  API did not respond within $maxWait s — check the WSL window for errors" -ForegroundColor Red
}

# ── 5. Wait for Vite to start (it prints its URL when ready) ──────────────────
Write-Host "Waiting for Vite..." -ForegroundColor Yellow
$viteReady = $false
$viteWait  = 0
while ($viteWait -lt 20) {
    Start-Sleep -Seconds 1
    $viteWait++
    $log = Receive-Job $webJob 2>&1
    if ($log -match "localhost:5173") {
        $viteReady = $true
        break
    }
}

if ($viteReady) {
    Write-Host "  Web UI is ready" -ForegroundColor Green
} else {
    Write-Host "  Vite starting (may still be installing dependencies)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  API   $apiUrl" -ForegroundColor White
Write-Host "  Web   $webUrl" -ForegroundColor White
Write-Host "  Key   dev-local-key" -ForegroundColor DarkGray
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

if (-not $NoBrowser -and $viteReady) {
    Start-Process $webUrl
}

Write-Host "Press Ctrl+C to stop the Web UI job." -ForegroundColor DarkGray
Write-Host "(Close the WSL window to stop the API.)" -ForegroundColor DarkGray
Write-Host ""

# Keep the script alive so the Vite job stays running; stream its output
try {
    while ($true) {
        $out = Receive-Job $webJob 2>&1
        if ($out) { $out | ForEach-Object { Write-Host "  [vite] $_" -ForegroundColor DarkCyan } }
        Start-Sleep -Milliseconds 500
    }
} finally {
    Stop-Job  $webJob -ErrorAction SilentlyContinue
    Remove-Job $webJob -ErrorAction SilentlyContinue
    Write-Host "Web UI stopped." -ForegroundColor Yellow
}
