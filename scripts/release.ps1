# release.ps1 -- Semver release script for Agentweaver.
#
# Usage:
#   pwsh -File .\scripts\release.ps1 [major|minor|patch]
#   pwsh -File .\scripts\release.ps1 --help
#
# Builds are deliberately delegated to 20-build-push-images.ps1. That script is
# the single owner of ACR build/retag behavior and stamps every resulting image
# with its required prov-<fullsha> provenance tag.

[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [string]$Bump,
  [switch]$DryRun,
  [Alias("h")]
  [switch]$Help
)

$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$AksScriptDir = Join-Path $ScriptDir "aks"
$BuildImagesScript = Join-Path $AksScriptDir "20-build-push-images.ps1"
$DeployScript = Join-Path $AksScriptDir "30-deploy.ps1"
$IsDryRun = $DryRun -or $env:DRY_RUN -eq "true"

function Show-ReleaseHelp {
  @"
release.ps1 -- Agentweaver semver release script

Usage:
  pwsh -File .\scripts\release.ps1 [major|minor|patch]

Arguments:
  major   Bump the major version (e.g. 0.6.0 -> 1.0.0)
  minor   Bump the minor version (e.g. 0.6.0 -> 0.7.0)
  patch   Bump the patch version (e.g. 0.6.0 -> 0.6.1)

Options and environment variables:
  -DryRun / DRY_RUN=true  Print actions without making changes
  IDENTITY_CLIENT_ID       Azure workload identity client ID for deploy
  TENANT_ID                Azure tenant ID for deploy

The release creates and pushes a version tag, creates the GitHub Release, then
delegates image build/retag/provenance stamping to
scripts\aks\20-build-push-images.ps1 before deploying with
scripts\aks\30-deploy.ps1.
"@ | Write-Host
}

function Invoke-ReleaseCommand {
  param(
    [Parameter(Mandatory)] [string]$Description,
    [Parameter(Mandatory)] [scriptblock]$Command
  )

  if ($IsDryRun) {
    Write-Host "  [dry-run] $Description"
    return
  }

  & $Command
  if ($LASTEXITCODE -ne 0) {
    throw "$Description failed (exit $LASTEXITCODE)"
  }
}

if ($Help -or $Bump -in @("--help", "-h", "help")) {
  Show-ReleaseHelp
  exit 0
}

if ($Bump -notin @("major", "minor", "patch")) {
  Write-Error "ERROR: argument must be one of: major, minor, patch. Run 'pwsh -File .\scripts\release.ps1 --help' for usage."
  exit 1
}

if (-not (Test-Path $BuildImagesScript)) {
  throw "ERROR: image build script not found: $BuildImagesScript"
}
if (-not (Test-Path $DeployScript)) {
  throw "ERROR: deploy script not found: $DeployScript"
}

Set-Location $RepoRoot

# 1. Validate clean working tree.
Write-Host "==> Checking working tree..."
git diff --quiet
if ($LASTEXITCODE -ne 0) {
  throw "ERROR: working tree has uncommitted changes. Commit or stash first."
}
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
  throw "ERROR: working tree has uncommitted changes. Commit or stash first."
}

# 2. Read and bump version.
$VersionFile = Join-Path $RepoRoot "VERSION"
if (-not (Test-Path $VersionFile)) {
  throw "ERROR: VERSION file not found at $VersionFile"
}

$CurrentVersion = ((Get-Content -Raw $VersionFile) -replace '\s', '')
if ($CurrentVersion -notmatch '^\d+\.\d+\.\d+$') {
  throw "ERROR: VERSION file contains invalid semver: '$CurrentVersion'"
}

[long]$Major, [long]$Minor, [long]$Patch = $CurrentVersion -split '\.'
switch ($Bump) {
  "major" { $Major++; $Minor = 0; $Patch = 0 }
  "minor" { $Minor++; $Patch = 0 }
  "patch" { $Patch++ }
}
$NewVersion = "$Major.$Minor.$Patch"
$NewTag = "v$NewVersion"

Write-Host "==> Bumping version: $CurrentVersion -> $NewVersion ($Bump)"

# Find the previous release before creating this tag. It is both the changelog
# baseline and the source tag passed to the shared image build planner.
$LastTag = (git describe --tags --abbrev=0 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $LastTag) {
  Write-Host "  (no previous tag found; treating first commit as baseline)"
  $LastTag = ""
  $LastTagDate = "1970-01-01T00:00:00Z"
} else {
  $LastTag = $LastTag.Trim()
  Write-Host "  Last tag: $LastTag"
  $LastTagDate = (git log -1 --format=%aI $LastTag).Trim()
  if ($LASTEXITCODE -ne 0 -or -not $LastTagDate) {
    throw "Could not determine date for previous tag $LastTag"
  }
}

# 3. Write VERSION and commit.
Write-Host "==> Writing VERSION file..."
if ($IsDryRun) {
  Write-Host "  [dry-run] Write $NewVersion to VERSION"
} else {
  Set-Content -Path $VersionFile -Value $NewVersion -Encoding ascii
}

Write-Host "==> Committing version bump..."
Invoke-ReleaseCommand -Description "git add VERSION" -Command { git add $VersionFile }
$CommitMessage = "chore(release): bump version to $NewTag"
Invoke-ReleaseCommand -Description "git commit version bump" -Command { git commit -m $CommitMessage }

# 4. Create annotated tag.
Write-Host "==> Creating annotated tag $NewTag..."
$TagMessage = "Release $NewTag"
Invoke-ReleaseCommand -Description "git tag $NewTag" -Command { git tag -a $NewTag -m $TagMessage }

# 5. Push release commit and tag.
Write-Host "==> Pushing release commit and tag to origin..."
Invoke-ReleaseCommand -Description "git push origin HEAD" -Command { git push origin HEAD }
Invoke-ReleaseCommand -Description "git push origin $NewTag" -Command { git push origin $NewTag }

# 6. Generate changelog from merged PRs.
Write-Host "==> Generating changelog from merged PRs since $LastTagDate..."
$ChangeLog = ""
if (Get-Command gh -ErrorAction SilentlyContinue) {
  $ChangeLogLines = @(gh pr list `
    --repo sabbour/agentweaver `
    --state merged `
    --search "merged:>$LastTagDate" `
    --json number,title,mergedAt `
    --jq '.[] | "- \(.title) (#\(.number))"' `
    2>$null)
  if ($LASTEXITCODE -eq 0) {
    $ChangeLog = ($ChangeLogLines -join [Environment]::NewLine).Trim()
  }
}
if (-not $ChangeLog) {
  $ChangeLog = "No pull requests found since $LastTag."
}
Write-Host $ChangeLog

# 7. Create GitHub Release.
Write-Host "==> Creating GitHub release $NewTag..."
Invoke-ReleaseCommand -Description "gh release create $NewTag" -Command {
  gh release create $NewTag --title $NewTag --notes $ChangeLog
}

# 8. Build or retag images through the shared provenance-aware image pipeline.
# PREVIOUS_IMAGE_TAG gives 20-build-push-images.ps1 the same baseline that
# release.sh used for its changed-image decision. TARGET_GIT_REF pins its
# comparison to the just-created release tag rather than ambient HEAD.
Write-Host ""
Write-Host "==> Processing images for $NewTag (previous: $(if ($LastTag) { $LastTag } else { 'none' }))..."
$env:IMAGE_TAG = $NewTag
$env:AGENTHOST_IMAGE_TAG = $NewTag
$env:TARGET_GIT_REF = $NewTag
if ($LastTag) {
  $env:PREVIOUS_IMAGE_TAG = $LastTag
} else {
  Remove-Item Env:\PREVIOUS_IMAGE_TAG -ErrorAction SilentlyContinue
}
if ($IsDryRun) { $env:DRY_RUN = "true" }

# Dot-source shared values here, after setting the release image tag, so
# standalone releases also resolve deployment identity settings when possible.
. (Join-Path $AksScriptDir "00-variables.ps1")

if ($IsDryRun) {
  Write-Host "  [dry-run] Invoke $BuildImagesScript -DryRun"
  & {
    $ErrorActionPreference = "Continue"
    & $BuildImagesScript -DryRun
  }
} else {
  & {
    # The build script explicitly handles expected non-zero native probes via
    # $LASTEXITCODE; preserve that behavior when invoked by this Stop-mode script.
    $ErrorActionPreference = "Continue"
    & $BuildImagesScript
  }
}
if ($LASTEXITCODE -ne 0) {
  throw "20-build-push-images.ps1 failed (exit $LASTEXITCODE)"
}

# 9. Deploy the release tag.
Write-Host ""
Write-Host "==> Deploying $NewTag to AKS..."
if ($IsDryRun) {
  Write-Host "  [dry-run] Invoke $DeployScript with IMAGE_TAG=$NewTag"
} else {
  & {
    $ErrorActionPreference = "Continue"
    & $DeployScript
  }
  if ($LASTEXITCODE -ne 0) {
    throw "30-deploy.ps1 failed (exit $LASTEXITCODE)"
  }
}

Write-Host ""
Write-Host "==================================================="
Write-Host " RELEASE $NewTag COMPLETE"
Write-Host "==================================================="
Write-Host ""
Write-Host "  GitHub Release: https://github.com/sabbour/agentweaver/releases/tag/$NewTag"
Write-Host ""
Write-Host "To verify what version is deployed:"
Write-Host "  kubectl get deployment agentweaver-api -n agentweaver \"
Write-Host "    -o jsonpath='{.spec.template.spec.containers[0].image}'"
