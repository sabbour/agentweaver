<#
.SYNOPSIS
    Start the Agentweavers development environment.

.DESCRIPTION
    Starts two processes:
      - Agentweaver.Api  — runs inside WSL2 using the Linux .NET 10 runtime
                          (picks up the bwrap sandbox executor automatically)
      - Web UI          — runs on Windows via Vite dev server

    The API listens on http://localhost:5000 (CORS allows localhost:8080).
    The Web UI listens on http://localhost:8080.

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
$apiProject = "apps/Agentweaver.Api"
$webDir     = Join-Path $repoRoot "apps\web"
$apiUrl     = "http://localhost:5000"
$webUrl     = "http://localhost:8080"
$apiPort    = ([Uri]$apiUrl).Port

# Convert Windows repo root to WSL path (C:\... -> /mnt/c/...)
$wslRepoRoot = ($repoRoot -replace '^([A-Za-z]):\\', { "/mnt/$($_.Groups[1].Value.ToLower())/" }) -replace '\\', '/'

Write-Host ""
Write-Host "  Agentweavers Dev" -ForegroundColor Cyan
Write-Host "  API  $apiUrl  (WSL2 / Linux .NET)" -ForegroundColor DarkCyan
Write-Host "  Web  $webUrl  (Windows / Vite)" -ForegroundColor DarkCyan
Write-Host ""

# ── 1. Kill any stale API processes/session in WSL ───────────────────────────
# The MAF FileSystemJsonCheckpointStore holds an exclusive lock on its directory,
# and the API binds port $apiPort. If a previous instance is still running (e.g.
# from an earlier dev session), the new one crashes immediately with
# "store already in use" (or the port is taken).
#
# `dotnet run --no-build` execs the Linux apphost (bin/.../Agentweaver.Api) as a
# CHILD process whose argv has NO "dotnet" prefix, so the old narrow pattern
# 'dotnet.*Agentweaver.Api' missed it and left the real API alive. Match the
# assembly name broadly (covers both the `dotnet run` parent and the apphost),
# then free the port directly as a fallback (fuser may be absent on minimal
# distros — the leading pkill handles those).
#
# The pattern is written '[A]gentweaver.Api' (a one-char regex class) so it still
# matches the running processes but NOT this very `bash -c` launcher line — that
# line contains '[A]gentweaver.Api' literally, not the substring 'Agentweaver.Api',
# so pkill won't SIGTERM its own shell before fuser/sleep run.
Write-Host "Stopping any existing API processes in WSL..." -ForegroundColor DarkGray
wsl --exec bash -c "pkill -f '[A]gentweaver.Api' 2>/dev/null; fuser -k ${apiPort}/tcp 2>/dev/null; sleep 1; true"

# ── 2. Build API inside WSL so the Linux apphost (ELF binary) is produced ────
# A Windows build produces Agentweaver.Api.exe but NOT the Linux apphost that
# `dotnet run --no-build` inside WSL2 needs. Building in WSL ensures the
# bin/Release/net10.0/Agentweaver.Api ELF binary is present before launch.
if (-not $SkipBuild) {
    Write-Host "Building API in WSL..." -ForegroundColor Yellow
    wsl --exec bash -c "cd '$wslRepoRoot' && dotnet build $apiProject -c Release -v q --nologo"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
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

$tmpSh    = Join-Path $env:TEMP "agentweaver-start-api.sh"
$wslTmpSh = ($tmpSh -replace '^([A-Za-z]):\\', { "/mnt/$($_.Groups[1].Value.ToLower())/" }) -replace '\\', '/'
# Write with LF-only line endings — PowerShell here-strings use CRLF on Windows
# and bash treats the \r as part of directory names, breaking cd.
[System.IO.File]::WriteAllText($tmpSh, ($bashScript -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

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
    if ($log -match "localhost:8080") { $viteReady = $true; break }
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

