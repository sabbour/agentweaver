<#
.SYNOPSIS
    Start the Agentweavers development environment.

.DESCRIPTION
    Starts two processes:
      - Agentweaver.Api  - runs inside WSL2 using the Linux .NET 10 runtime
                          (picks up the bwrap sandbox executor automatically)
      - Web UI          - runs on Windows via Vite dev server

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
$apiProject = "apps/Agentweaver.Api"
$webDir     = Join-Path $repoRoot "apps\web"
$apiUrl     = "http://localhost:5000"
$webUrl     = "http://localhost:5173"
$apiPort    = ([Uri]$apiUrl).Port
$apiDisplay = "localhost:$apiPort"

function ConvertTo-WslPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.Length -lt 3 -or $fullPath[1] -ne ':') {
        throw "Expected an absolute Windows drive path, got '$Path'."
    }

    $drive = $fullPath.Substring(0, 1).ToLowerInvariant()
    $rest = $fullPath.Substring(2).TrimStart('\') -replace '\\', '/'
    return "/mnt/$drive/$rest"
}

function Read-DotEnvFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $values }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*(#|$)') { continue }
        if ($line -notmatch '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_:]*)\s*=\s*(.*)\s*$') { continue }

        $key = $matches[1]
        $value = $matches[2].Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        $values[$key] = $value
    }

    return $values
}

function Read-UserSecrets {
    param([Parameter(Mandatory = $true)][string] $ProjectPath)

    $values = @{}
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $values }
    if (-not (Test-Path -LiteralPath $ProjectPath)) { return $values }

    $lines = & dotnet user-secrets list --project $ProjectPath 2>$null
    foreach ($line in $lines) {
        if ($line -match '^\s*([^=]+?)\s*=\s*(.*)$') {
            $values[$matches[1].Trim()] = $matches[2]
        }
    }

    return $values
}

function Merge-ConfigValues {
    param(
        [Parameter(Mandatory = $true)][hashtable] $Target,
        [Parameter(Mandatory = $true)][hashtable] $Source,
        [Parameter(Mandatory = $true)][string] $SourceName,
        [Parameter(Mandatory = $true)][hashtable] $Sources
    )

    foreach ($key in $Source.Keys) {
        if ([string]::IsNullOrWhiteSpace([string]$Source[$key])) { continue }
        $Target[$key] = [string]$Source[$key]
        $Sources[$key] = $SourceName
    }
}

function Get-LocalConfigValues {
    $values = @{}
    $sources = @{}

    # Lowest precedence: API user-secrets. These are local-only and never printed.
    $apiCsproj = Join-Path $repoRoot "apps\Agentweaver.Api\Agentweaver.Api.csproj"
    Merge-ConfigValues $values (Read-UserSecrets $apiCsproj) "user-secrets" $sources

    # Local env files are ignored by git. .env.local intentionally wins over .env.
    foreach ($envFileName in @(".env", ".env.local")) {
        $envFile = Join-Path $repoRoot $envFileName
        Merge-ConfigValues $values (Read-DotEnvFile $envFile) $envFileName $sources
    }

    # Highest precedence: explicit environment variables in this PowerShell session.
    foreach ($entry in [Environment]::GetEnvironmentVariables("Process").GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Value)) { continue }
        $values[[string]$entry.Key] = [string]$entry.Value
        $sources[[string]$entry.Key] = "process environment"
    }

    return [pscustomobject]@{ Values = $values; Sources = $sources }
}

function Resolve-LocalSetting {
    param(
        [Parameter(Mandatory = $true)] [hashtable] $Values,
        [Parameter(Mandatory = $true)] [hashtable] $Sources,
        [Parameter(Mandatory = $true)] [string[]] $Keys
    )

    foreach ($key in $Keys) {
        if ($Values.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace($Values[$key])) {
            return [pscustomobject]@{ Key = $key; Value = [string]$Values[$key]; Source = [string]$Sources[$key] }
        }
    }

    return $null
}

function Add-WslEnvVariable {
    param([Parameter(Mandatory = $true)][string] $Name)

    $entry = "$Name/u"
    $existing = @()
    if (-not [string]::IsNullOrWhiteSpace($env:WSLENV)) {
        $existing = $env:WSLENV -split ':'
    }

    if ($existing -notcontains $entry) {
        $env:WSLENV = (@($existing) + $entry | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ':'
    }
}

function Enable-AppInsightsWslBridge {
    $config = Get-LocalConfigValues

    $connection = Resolve-LocalSetting $config.Values $config.Sources @(
        "APPLICATIONINSIGHTS_CONNECTION_STRING",
        "ApplicationInsights:ConnectionString",
        "ApplicationInsights__ConnectionString",
        "APPINSIGHTS_CONNECTION_STRING",
        "APP_INSIGHTS_CONNECTION_STRING"
    )

    $connectionFromLegacyKey = $false
    if ($null -eq $connection) {
        $instrumentationKey = Resolve-LocalSetting $config.Values $config.Sources @(
            "APPLICATIONINSIGHTS_INSTRUMENTATIONKEY",
            "ApplicationInsights:InstrumentationKey",
            "ApplicationInsights__InstrumentationKey",
            "APPINSIGHTS_INSTRUMENTATIONKEY",
            "APP_INSIGHTS_INSTRUMENTATIONKEY"
        )

        if ($null -ne $instrumentationKey) {
            $connection = [pscustomobject]@{
                Key = $instrumentationKey.Key
                Value = "InstrumentationKey=$($instrumentationKey.Value)"
                Source = $instrumentationKey.Source
            }
            $connectionFromLegacyKey = $true
        }
    }

    $workspace = Resolve-LocalSetting $config.Values $config.Sources @(
        "APPLICATIONINSIGHTS_WORKSPACE_ID",
        "ApplicationInsights:WorkspaceId",
        "ApplicationInsights__WorkspaceId",
        "APPINSIGHTS_WORKSPACE_ID",
        "APP_INSIGHTS_WORKSPACE_ID"
    )

    if ($null -ne $connection) {
        $env:APPLICATIONINSIGHTS_CONNECTION_STRING = $connection.Value
        Add-WslEnvVariable "APPLICATIONINSIGHTS_CONNECTION_STRING"
    }

    if ($null -ne $workspace) {
        $env:APPLICATIONINSIGHTS_WORKSPACE_ID = $workspace.Value
        Add-WslEnvVariable "APPLICATIONINSIGHTS_WORKSPACE_ID"
    }

    $connectionStatus = if ($null -ne $connection) { "configured from $($connection.Source) key '$($connection.Key)'" } else { "missing" }
    if ($connectionFromLegacyKey) {
        $connectionStatus += " (legacy instrumentation key mapped to connection string)"
    }
    $workspaceStatus = if ($null -ne $workspace) { "configured from $($workspace.Source) key '$($workspace.Key)'" } else { "missing" }

    Write-Host "AppInsights local config: connection string $connectionStatus; workspace id $workspaceStatus." -ForegroundColor DarkGray
}

# Convert Windows repo root to WSL path (C:\... -> /mnt/c/...)
$wslRepoRoot = ConvertTo-WslPath $repoRoot

Write-Host ""
Write-Host "  Agentweavers Dev" -ForegroundColor Cyan
Write-Host "  API  $apiDisplay  (WSL2 / Linux .NET; health/API only)" -ForegroundColor DarkCyan
Write-Host "  Web  $webUrl  (Windows / Vite)" -ForegroundColor DarkCyan
Write-Host ""

# If local App Insights values live in ignored env files or API user-secrets on Windows,
# bridge them into the WSL API process under the canonical Azure Monitor env names.
Enable-AppInsightsWslBridge

# -- 1. Kill any stale API processes/session in WSL ---------------------------
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
# distros - the leading pkill handles those).
#
# The pattern is written '[A]gentweaver.Api' (a one-char regex class) so it still
# matches the running processes but NOT this very `bash -c` launcher line - that
# line contains '[A]gentweaver.Api' literally, not the substring 'Agentweaver.Api',
# so pkill won't SIGTERM its own shell before fuser/sleep run.
Write-Host "Stopping any existing API processes in WSL..." -ForegroundColor DarkGray
wsl --exec bash -c "pkill -f '[A]gentweaver.Api' 2>/dev/null; fuser -k ${apiPort}/tcp 2>/dev/null; sleep 1; true"

# -- 2. Build API inside WSL so the Linux apphost (ELF binary) is produced ----
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

# -- 3. Write a bash launcher script to a temp file ---------------------------
#
# Windows Terminal parses its argument string and splits on semicolons, so
# passing a compound bash -c "cmd1; cmd2; cmd3" via Start-Process wt causes
# each semicolon-separated fragment to become its own WT tab/pane.
# Writing to a .sh file sidesteps all quoting/splitting issues.
#
$bashScriptLines = New-Object System.Collections.Generic.List[string]
$bashScriptLines.Add("#!/bin/bash")
$bashScriptLines.Add("cd '$wslRepoRoot'")
$bashScriptLines.Add("export ASPNETCORE_ENVIRONMENT=Development")
$bashScriptLines.Add("# APPLICATIONINSIGHTS_* env vars are imported from PowerShell via WSLENV when configured.")
$bashScriptLines.Add("dotnet run --project $apiProject --configuration Release --urls $apiUrl --no-build")
$bashScriptLines.Add('echo ""')
$bashScriptLines.Add('echo "API process exited (code: $?). Press Enter to close."')
$bashScriptLines.Add("read")
$bashScript = $bashScriptLines -join "`n"

$tmpSh    = Join-Path $env:TEMP "agentweaver-start-api.sh"
$wslTmpSh = ConvertTo-WslPath $tmpSh
# Write with LF-only line endings - PowerShell here-strings use CRLF on Windows
# and bash treats the \r as part of directory names, breaking cd.
[System.IO.File]::WriteAllText($tmpSh, ($bashScript -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

# -- 4. Start API inside WSL2 -------------------------------------------------
Write-Host "Starting API in WSL2..." -ForegroundColor Yellow

$wtAvailable = $null -ne (Get-Command wt -ErrorAction SilentlyContinue)
if ($wtAvailable) {
    # wt new-tab -- wsl bash /path/to/script.sh
    # The '--' stops wt from interpreting further args as its own commands.
    Start-Process wt -ArgumentList @("new-tab", "--", "wsl", "bash", $wslTmpSh)
} else {
    Start-Process wsl -ArgumentList @("bash", $wslTmpSh)
}

# -- 5. Start Web UI on Windows ----------------------------------------------
Write-Host "Starting Web UI (Vite)..." -ForegroundColor Yellow

$npmStart = {
    param($dir)
    Set-Location $dir
    npm run dev -- --force
}

$webJob = Start-Job -ScriptBlock $npmStart -ArgumentList $webDir

# -- 6. Wait for API readiness ------------------------------------------------
Write-Host ""
Write-Host "Waiting for API health endpoint on $apiDisplay ..." -ForegroundColor Yellow

$maxWait  = 60
$elapsed  = 0
$apiReady = $false

while ($elapsed -lt $maxWait) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    try {
        # Probe the unauthenticated /health endpoint. The root path "/" now
        # requires auth and returns 401, which made the old 200-only check
        # report a false "did not respond" timeout even though the API was up.
        $resp = Invoke-WebRequest -Uri "$apiUrl/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $apiReady = $true; break }
    } catch {
        Write-Host "  ... ($elapsed s)" -ForegroundColor DarkGray
    }
}

Write-Host ""
if ($apiReady) {
    Write-Host "  API is ready" -ForegroundColor Green
} else {
    Write-Host "  API did not respond within $maxWait s - check the WSL window for errors" -ForegroundColor Red
}

# -- 7. Wait for Vite ---------------------------------------------------------
Write-Host "Waiting for Vite..." -ForegroundColor Yellow
$viteReady = $false
$viteWait  = 0
while ($viteWait -lt 20) {
    Start-Sleep -Seconds 1
    $viteWait++
    $log = Receive-Job $webJob -ErrorAction Continue 2>&1
    if ($log -match "localhost:5173") { $viteReady = $true; break }
}

if ($viteReady) {
    Write-Host "  Web UI is ready" -ForegroundColor Green
} else {
    Write-Host "  Vite starting (may still be installing dependencies)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "----------------------------------------------" -ForegroundColor Cyan
Write-Host "  API   $apiDisplay (not a browser page)" -ForegroundColor White
Write-Host "  Web   $webUrl" -ForegroundColor White
Write-Host "  Key   dev-local-key" -ForegroundColor DarkGray
Write-Host "----------------------------------------------" -ForegroundColor Cyan
Write-Host ""

if (-not $NoBrowser -and $viteReady) {
    Start-Process $webUrl
}

Write-Host "Press Ctrl+C to stop the Web UI job." -ForegroundColor DarkGray
Write-Host "(Close the WSL window to stop the API.)" -ForegroundColor DarkGray
Write-Host ""

try {
    while ($true) {
        $out = Receive-Job $webJob -ErrorAction Continue 2>&1
        if ($out) { $out | ForEach-Object { Write-Host "  [vite] $_" -ForegroundColor DarkCyan } }
        Start-Sleep -Milliseconds 500
    }
} finally {
    Stop-Job  $webJob -ErrorAction SilentlyContinue
    Remove-Job $webJob -ErrorAction SilentlyContinue
    Write-Host "Web UI stopped." -ForegroundColor Yellow
}
