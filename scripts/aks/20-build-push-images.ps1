# 20-build-push-images.ps1 -- Build and push Agentweaver container images to ACR.
# Keep in sync with 20-build-push-images.sh (bash equivalent).
#
# Builds four images using 'az acr build' (no local Docker daemon required), or
# retags unchanged images with 'az acr import' when a previous deployed tag is known:
#   agentweaver-api      -- .NET 10 API         (context: repo root, Dockerfile: apps/Agentweaver.Api/Dockerfile)
#   agentweaver-frontend -- ASP.NET Core + SPA   (context: repo root, Dockerfile: apps/web/Dockerfile)
#   agentweaver-mcp      -- MCP server           (context: repo root, Dockerfile: apps/Agentweaver.Mcp/Dockerfile)
#   agentweaver-agent-host -- pod-per-run AgentHost (context: repo root, Dockerfile: apps/Agentweaver.AgentHost/Dockerfile)
#
# All images use the repo root as build context because their Dockerfiles reference
# multiple subdirectories.
#
# Usage:
#   . .\scripts\aks\00-variables.ps1
#   .\scripts\aks\20-build-push-images.ps1
#
# Optional:
#   .\scripts\aks\20-build-push-images.ps1 -DryRun
#   $env:PREVIOUS_IMAGE_TAG = "vX.Y.Z"; .\scripts\aks\20-build-push-images.ps1

[CmdletBinding()]
param(
  [switch]$DryRun,
  [switch]$ForceRebuild
)

# NOTE: deliberately NOT setting $ErrorActionPreference = "Stop" here. Windows
# PowerShell 5.1 (the default `powershell.exe` most Git-Bash-free workflows run
# under) treats ANY stderr line from a native command as a terminating
# ErrorRecord when $ErrorActionPreference is "Stop" -- even when that stream is
# explicitly redirected with `2>$null`. That turns routine, already-handled
# failures (e.g. `git rev-parse --verify <bad-ref>` while probing whether a ref
# exists) into uncatchable script-ending exceptions. Every git/az invocation
# below already checks $LASTEXITCODE explicitly (the PowerShell analogue of
# bash's `set -e`), so a global Stop preference is not needed and actively
# breaks normal, expected non-zero-exit probing.

# NOTE: unlike the bash version's `az()` shell-function wrapper (needed to work
# around Python's -I isolated-mode launcher discarding PYTHONUTF8/PYTHONIOENCODING
# when stdout is redirected under Git Bash), native PowerShell's pipeline already
# communicates with child processes over UTF-16/UTF-8 console handles rather than
# a POSIX pipe with the host ANSI code page, so `az` does not hit the same
# UnicodeEncodeError here. No AZ_PYEXE-style wrapper is required in this port.

$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
$FrontendNodeModulesDir = Join-Path $RepoRoot "apps\web\node_modules"
$FrontendNodeModulesBackupDir = "$RepoRoot.frontend-node_modules.$PID"

# shellcheck-equivalent: source=00-variables.ps1
. (Join-Path $ScriptDir "00-variables.ps1")
# Shared build/retag/stamp functions, also dot-sourced by the background jobs
# this script launches (see Invoke-ScheduleImage below).
. (Join-Path $ScriptDir "_image-functions.ps1")

function Remove-FrontendNpmrcBuild {
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $RepoRoot "apps\web\.npmrc.build")
}

function Restore-FrontendNodeModules {
  if (-not (Test-Path $FrontendNodeModulesBackupDir)) { return }
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $FrontendNodeModulesDir
  Move-Item -Force $FrontendNodeModulesBackupDir $FrontendNodeModulesDir
}

function Invoke-CleanupFrontendBuildArtifacts {
  Remove-FrontendNpmrcBuild
  Restore-FrontendNodeModules
}

# PowerShell equivalent of bash's `trap cleanup_frontend_build_artifacts EXIT`.
try {

if ($DryRun) { $env:DRY_RUN = "true" }
if ($ForceRebuild) { $env:FORCE_REBUILD = "true" }

$TargetGitRef = if ($env:TARGET_GIT_REF) { $env:TARGET_GIT_REF } else { $env:IMAGE_TAG }
$IsDryRun = ($env:DRY_RUN -eq "true")
$IsForceRebuild = ($env:FORCE_REBUILD -eq "true")

Write-Host ""
Write-Host "=== Building, retagging, and pushing Agentweaver images ==="
Write-Host "  ACR:                 $($env:ACR_LOGIN_SERVER)"
Write-Host "  Image tag:           $($env:IMAGE_TAG)"
Write-Host "  AgentHost image tag: $($env:AGENTHOST_IMAGE_TAG)"
Write-Host ""
Write-Host "  Redeploy efficiency:"
Write-Host "    - If PREVIOUS_IMAGE_TAG or a current cluster image tag is available, unchanged"
Write-Host "      images are retagged with 'az acr import' instead of rebuilt."
Write-Host "    - Changed images are built in parallel with 'az acr build'."
Write-Host "    - Set -ForceRebuild (or `$env:FORCE_REBUILD=true`) to rebuild every image."
Write-Host "    - Set -DryRun (or `$env:DRY_RUN=true`) to print the build/retag plan without invoking ACR or npm."
Write-Host "    - Every build/retag is also stamped with a 'prov-<fullsha>' ACR tag recording"
Write-Host "      the commit its content actually corresponds to (verify with"
Write-Host "      scripts/aks/25-verify-image-provenance.ps1 after 30-deploy.ps1)."
Write-Host "    - Provenance stamping is REQUIRED: if the extra prov tag cannot be written,"
Write-Host "      the image job fails rather than shipping an unverifiable release artifact."
Write-Host ""

Set-Location $RepoRoot

# NOTE: Get-CurrentDeploymentTag, Get-CurrentAgentHostTag, Get-ReleaseRefForTag,
# Get-SourceCommitForTag, and Test-PathsChanged are defined in _image-functions.ps1
# (dot-sourced above), so the exact same function bodies are available both here
# and inside the background jobs launched by Invoke-ScheduleImage below.

$resolvedTarget = (git rev-parse --verify "$TargetGitRef^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $resolvedTarget) {
  $resolvedTarget = Get-ReleaseRefForTag -Tag $env:IMAGE_TAG
}
if (-not $resolvedTarget) {
  $resolvedTarget = (git rev-parse HEAD).Trim()
}
$TargetCommit = $resolvedTarget.Trim()

function Get-FrontendNpmPasswordB64 {
  if ($env:AZURE_ARTIFACTS_NPM_PASSWORD_B64) { return $env:AZURE_ARTIFACTS_NPM_PASSWORD_B64 }
  if ($env:AZURE_ARTIFACTS_NPM_PAT) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($env:AZURE_ARTIFACTS_NPM_PAT)
    return [Convert]::ToBase64String($bytes)
  }
  return $null
}

function Get-FrontendNpmUserconfig {
  $homeNpmrc = Join-Path $env:USERPROFILE ".npmrc"
  $buildNpmrc = Join-Path $RepoRoot "apps\web\.npmrc.build"

  $passwordB64 = Get-FrontendNpmPasswordB64
  if ($passwordB64) {
    Copy-Item (Join-Path $RepoRoot "apps\web\.npmrc") $buildNpmrc -Force
    @(
      '; begin auth token'
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/registry/:username=agentweaver'
      "//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/registry/:_password=$passwordB64"
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/registry/:email=npm requires email to be set but does not use the value'
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/:username=agentweaver'
      "//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/:_password=$passwordB64"
      '//pkgs.dev.azure.com/office/Office/_packaging/1JS/npm/:email=npm requires email to be set but does not use the value'
      '; end auth token'
    ) | Add-Content -Path $buildNpmrc
    return $buildNpmrc
  }

  if ((Test-Path $homeNpmrc) -and (Select-String -Path $homeNpmrc -Pattern '^//pkgs\.dev\.azure\.com/office/Office/_packaging/1JS/npm(/registry)?/:_password=' -Quiet)) {
    return $homeNpmrc
  }

  return $null
}

function Invoke-FrontendNpmCredentialProvider {
  # PowerShell/native Windows path: the ado-npm-auth fallback works fine here
  # (the bash version only blocks this on Linux/WSL due to a RID-specific
  # credential-provider asset mismatch under gzip decoding).
  $env:npm_config_registry = "https://registry.npmjs.org"
  npx --yes ado-npm-auth -c (Join-Path $RepoRoot "apps\web\.npmrc")
}

function Move-FrontendNodeModulesOutsideAcrContext {
  if (-not (Test-Path $FrontendNodeModulesDir)) { return }
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $FrontendNodeModulesBackupDir
  Move-Item -Force $FrontendNodeModulesDir $FrontendNodeModulesBackupDir
  Write-Host "  [frontend] Temporarily moved node_modules out of the ACR build context"
}

function Invoke-PrepareFrontendDist {
  if ($IsDryRun) {
    Write-Host "  [dry-run] Would build local frontend assets before ACR build"
    return
  }

  if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "ERROR: npm is required to build apps/web before az acr build."
  }

  Write-Host "--- Building local frontend assets for agentweaver-frontend ---"
  $userconfig = Get-FrontendNpmUserconfig
  if ($userconfig) {
    Write-Host "  [frontend] Using PAT-backed npm userconfig outside the Docker context"
  } else {
    Write-Host "  [frontend] No PAT-backed npm userconfig found; attempting interactive auth helper"
  }

  Push-Location (Join-Path $RepoRoot "apps\web")
  try {
    if ($userconfig) {
      $env:NPM_CONFIG_USERCONFIG = $userconfig
      npm ci --legacy-peer-deps
    } else {
      Invoke-FrontendNpmCredentialProvider
      npm ci --legacy-peer-deps
    }
    Remove-Item Env:\VITE_API_URL -ErrorAction SilentlyContinue
    Remove-Item Env:\VITE_API_KEY -ErrorAction SilentlyContinue
    npm run build
  } finally {
    if ($userconfig) { Remove-Item Env:\NPM_CONFIG_USERCONFIG -ErrorAction SilentlyContinue }
    Pop-Location
  }

  Remove-FrontendNpmrcBuild
  # Keep prebuilt dist/ but move node_modules out of the repo before az acr build:
  # az's context tar step can choke on broken symlinks even when .dockerignore excludes them.
  Move-FrontendNodeModulesOutsideAcrContext
}

# NOTE: Wait-ForAcrTagDigest, Invoke-StampProvenance, Invoke-RetagImage, and
# Invoke-BuildImage are defined in _image-functions.ps1 (dot-sourced above) so
# the exact same bodies run here and inside the background jobs below. Those
# functions read $env:DRY_RUN directly rather than a script-scoped $IsDryRun,
# since background jobs run in a separate process that only inherits $env:*.

$CommonDotnetPaths = @(
  "agentweaver.sln"
  "global.json"
  "Directory.Build.props"
  "Directory.Packages.props"
  "NuGet.config"
  "packages"
)

# Job bookkeeping. PowerShell background jobs (Start-Job) stand in for bash's
# `&`-backgrounded subshells + `wait -n`; each schedule step launches one job
# and we wait/collect them below, failing fast (and stopping the rest) the
# same way terminate_remaining_jobs()/wait_for_image_jobs() do in bash.
$Jobs = @()

function Invoke-ScheduleImage {
  param(
    [string]$Image,
    [string]$TargetTag,
    [string]$Dockerfile,
    [string]$DeployedTag,
    [string[]]$Paths
  )

  $sourceTag = if ($env:PREVIOUS_IMAGE_TAG) { $env:PREVIOUS_IMAGE_TAG } else { $DeployedTag }
  $sourceCommit = Get-SourceCommitForTag -Tag $sourceTag

  # Decide build vs. retag using the SAME logic as bash's schedule_image(), then
  # run the actual work in a background job so multiple images build in parallel.
  $action = $null
  if ($env:FORCE_REBUILD -eq "true" -or -not $sourceTag) {
    Write-Host "  [build]  $Image (forced or no previous image tag)"
    $action = "build"
  } elseif (-not $sourceCommit) {
    Write-Host "  [build]  $Image (previous tag $sourceTag has no resolvable VERSION commit)"
    $action = "build"
  } elseif (Test-PathsChanged -OldRef $sourceCommit -NewRef $TargetCommit -Paths $Paths) {
    Write-Host "  [build]  $Image (changed since $sourceTag at $($sourceCommit.Substring(0,12)))"
    $action = "build"
  } else {
    Write-Host "  [retag]  $Image (unchanged since $sourceTag at $($sourceCommit.Substring(0,12)))"
    $action = "retag"
  }

  # Use Start-Job (built into both Windows PowerShell 5.1 and PowerShell 7+),
  # NOT Start-ThreadJob -- the ThreadJob module ships with PowerShell 7's
  # default module path but is NOT present in Windows PowerShell 5.1, which is
  # what `powershell.exe` (the default on most machines) actually runs. Each
  # job is a separate child process, so $env:DRY_RUN/$env:FORCE_REBUILD/etc
  # are inherited automatically (process environment is copied at spawn time),
  # and 00-variables.ps1's "if not already set" guards make re-sourcing it
  # inside the job a no-op (no repeated `az` lookups).
  $job = Start-Job -ScriptBlock {
    param($Action, $Image, $TargetTag, $Dockerfile, $SourceTag, $SourceCommit, $TargetCommitIn, $RepoRootIn, $ScriptDirIn)
    Set-Location $RepoRootIn
    . (Join-Path $ScriptDirIn "00-variables.ps1") | Out-Null
    . (Join-Path $ScriptDirIn "_image-functions.ps1")
    if ($Action -eq "build") {
      Invoke-BuildImage -Image $Image -Tag $TargetTag -Dockerfile $Dockerfile -Commit $TargetCommitIn
    } else {
      if (-not (Invoke-RetagImage -Image $Image -SourceTag $SourceTag -TargetTag $TargetTag)) { throw "retag failed for ${Image}:${TargetTag}" }
      if (-not (Invoke-StampProvenance -Image $Image -Tag $TargetTag -Commit $SourceCommit)) { throw "stamp_provenance failed for ${Image}:${TargetTag}" }
    }
  } -ArgumentList $action, $Image, $TargetTag, $Dockerfile, $sourceTag, $sourceCommit, $TargetCommit, $RepoRoot, $ScriptDir

  $script:Jobs += [PSCustomObject]@{ Job = $job; Name = "${Image}:${TargetTag}" }
}

$ApiDeployedTag = Get-CurrentDeploymentTag -Deployment "agentweaver-api"
$FrontendDeployedTag = Get-CurrentDeploymentTag -Deployment "agentweaver-frontend"
$McpDeployedTag = Get-CurrentDeploymentTag -Deployment "agentweaver-mcp"
$AgentHostDeployedTag = Get-CurrentAgentHostTag

$FrontendSourceTag = if ($env:PREVIOUS_IMAGE_TAG) { $env:PREVIOUS_IMAGE_TAG } else { $FrontendDeployedTag }
$FrontendSourceCommit = Get-SourceCommitForTag -Tag $FrontendSourceTag
$FrontendPathsChanged = (-not $FrontendSourceCommit) -or (Test-PathsChanged -OldRef $FrontendSourceCommit -NewRef $TargetCommit -Paths @("apps/web", "apps/Agentweaver.Web"))
if ($IsForceRebuild -or -not $FrontendSourceTag -or $FrontendPathsChanged) {
  # All images share the repo root as the ACR build context, so move frontend
  # node_modules out of that context before any parallel az acr build starts.
  Invoke-PrepareFrontendDist
}

Invoke-ScheduleImage -Image "agentweaver-api" -TargetTag $env:IMAGE_TAG -Dockerfile "apps/Agentweaver.Api/Dockerfile" -DeployedTag $ApiDeployedTag -Paths ($CommonDotnetPaths + @("apps/Agentweaver.Api"))
Invoke-ScheduleImage -Image "agentweaver-frontend" -TargetTag $env:IMAGE_TAG -Dockerfile "apps/web/Dockerfile" -DeployedTag $FrontendDeployedTag -Paths @("apps/web", "apps/Agentweaver.Web")
Invoke-ScheduleImage -Image "agentweaver-mcp" -TargetTag $env:IMAGE_TAG -Dockerfile "apps/Agentweaver.Mcp/Dockerfile" -DeployedTag $McpDeployedTag -Paths ($CommonDotnetPaths + @("apps/Agentweaver.Mcp"))
Invoke-ScheduleImage -Image "agentweaver-agent-host" -TargetTag $env:AGENTHOST_IMAGE_TAG -Dockerfile "apps/Agentweaver.AgentHost/Dockerfile" -DeployedTag $AgentHostDeployedTag -Paths ($CommonDotnetPaths + @("apps/Agentweaver.AgentHost"))

Write-Host ""
Write-Host "Waiting for image jobs to finish..."
# Mirrors bash's `wait -n` + terminate_remaining_jobs(): wait for whichever job
# finishes FIRST (not necessarily the first one launched), report it, and if it
# failed, stop every other still-running job before failing the whole script.
$failed = $false
$pending = [System.Collections.ArrayList]::new()
foreach ($entry in $Jobs) { [void]$pending.Add($entry) }

while ($pending.Count -gt 0) {
  $pendingJobs = $pending | ForEach-Object { $_.Job }
  $completedJob = Wait-Job -Job $pendingJobs -Any
  $entry = $pending | Where-Object { $_.Job.Id -eq $completedJob.Id } | Select-Object -First 1

  $jobErr = $null
  Receive-Job -Job $entry.Job -ErrorAction SilentlyContinue -ErrorVariable jobErr | Out-Null

  if ($entry.Job.State -eq "Failed") {
    $failureDetail = $null
    if ($entry.Job.ChildJobs.Count -gt 0 -and $entry.Job.ChildJobs[0].JobStateInfo.Reason) {
      $failureDetail = $entry.Job.ChildJobs[0].JobStateInfo.Reason.ToString()
    } elseif ($jobErr) {
      $failureDetail = ($jobErr | Out-String).Trim()
    }
    if (-not $failureDetail) { $failureDetail = "job failed" }
    Write-Host "  [FAIL] $($entry.Name): $failureDetail" -ForegroundColor Red
    $failed = $true
    # Stop any jobs still running, mirroring bash's terminate_remaining_jobs().
    foreach ($other in $pending) {
      if ($other.Job.Id -ne $entry.Job.Id -and $other.Job.State -eq "Running") {
        Write-Host "  [STOP] $($other.Name)"
        Stop-Job -Job $other.Job -ErrorAction SilentlyContinue
      }
    }
    break
  } else {
    Write-Host "  [OK] $($entry.Name)"
  }

  $pending.Remove($entry)
}

foreach ($entry in $Jobs) { Remove-Job -Job $entry.Job -Force -ErrorAction SilentlyContinue }

if ($failed) {
  Write-Error "ERROR: one or more image jobs failed."
  exit 1
}

# -- Summary ------------------------------------------------------------------
Write-Host ""
Write-Host "==================================================="
Write-Host " IMAGES READY IN ACR"
Write-Host "==================================================="
Write-Host ""
Write-Host "  $($env:ACR_LOGIN_SERVER)/agentweaver-api:$($env:IMAGE_TAG)"
Write-Host "  $($env:ACR_LOGIN_SERVER)/agentweaver-frontend:$($env:IMAGE_TAG)"
Write-Host "  $($env:ACR_LOGIN_SERVER)/agentweaver-mcp:$($env:IMAGE_TAG)"
Write-Host "  $($env:ACR_LOGIN_SERVER)/agentweaver-agent-host:$($env:AGENTHOST_IMAGE_TAG)"
Write-Host ""
Write-Host "Export for deploy step:"
Write-Host "  `$env:ACR_NAME='$($env:ACR_NAME)'"
Write-Host "  `$env:IMAGE_TAG='$($env:IMAGE_TAG)'"
Write-Host "  `$env:AGENTHOST_IMAGE_TAG='$($env:AGENTHOST_IMAGE_TAG)'"
Write-Host ""
Write-Host "  Next step:"
Write-Host "    .\scripts\aks\30-deploy.ps1"

} finally {
  Invoke-CleanupFrontendBuildArtifacts
}
