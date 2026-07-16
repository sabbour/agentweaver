# 25-verify-image-provenance.ps1 -- Independent, post-deploy safety net for #251
# ("release image retag-forward can ship stale code").
# Keep in sync with 25-verify-image-provenance.sh (bash equivalent).
#
# 20-build-push-images.ps1 decides build-vs-retag at build time and, since the
# #251/#303 fix, stamps every image it produces with an extra immutable ACR
# tag 'prov-<sha>' recording the commit its content actually corresponds
# to. This script re-checks that decision independently and *after* the fact:
# for each of the 4 workloads it finds the prov-<sha> tag pointing at the
# exact digest that is CURRENTLY RUNNING in live pods (per api/frontend/mcp
# Deployments and running agent-host pods), then verifies that commit has no
# diff in the paths that feed that image, versus the target commit being
# verified (HEAD by default, or VERIFY_GIT_REF).
#
# This deliberately does NOT re-derive or trust 20-build-push-images.ps1's own
# in-process TargetCommit/Test-PathsChanged() decision -- it re-derives
# everything from what is actually running in ACR/AKS right now, so it also
# catches: a manual 'az acr import' done outside the script, deploying a tag
# that was never re-verified, or a bug in the build script itself.
#
# Usage:
#   . .\scripts\aks\00-variables.ps1
#   .\scripts\aks\25-verify-image-provenance.ps1
#
# Optional:
#   $env:VERIFY_GIT_REF = "<ref>"   Commit/ref to diff running images against (default: HEAD)

[CmdletBinding()]
param()

$ScriptDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path
. (Join-Path $ScriptDir "00-variables.ps1")
# Shared verification helpers, extracted so they can be unit-tested in
# isolation (see scripts/aks/tests/25-verify-image-provenance.Tests.ps1).
. (Join-Path $ScriptDir "_provenance-functions.ps1")
Set-Location $RepoRoot

$VerifyGitRef = if ($env:VERIFY_GIT_REF) { $env:VERIFY_GIT_REF } else { "HEAD" }

# Defensive check: VERIFY_GIT_REF is sometimes passed in from a caller (e.g.
# 30-deploy.ps1) as an IMAGE_TAG-derived string. Since ~v0.9.36, release tags
# are no longer created in git for every VERSION bump (see Get-ReleaseRefForTag
# in _image-functions.ps1/20-build-push-images.sh), so a caller-supplied ref
# may not resolve. Fail with a clear, actionable message instead of git's
# generic "fatal: Needed a single revision".
$VerifyCommit = (git rev-parse --verify "$VerifyGitRef^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $VerifyCommit) {
  Write-Host "ERROR: VERIFY_GIT_REF='$VerifyGitRef' does not resolve to a commit in this repository." -ForegroundColor Red
  Write-Host "  This is usually because VERIFY_GIT_REF was derived from IMAGE_TAG (a VERSION-file" -ForegroundColor Red
  Write-Host "  semver string), which is not necessarily an actual git tag/ref. Pass an explicit," -ForegroundColor Red
  Write-Host "  resolvable commit/ref via `$env:VERIFY_GIT_REF, or leave it unset to default to HEAD." -ForegroundColor Red
  exit 1
}
$VerifyCommit = $VerifyCommit.Trim()

# Reset pass/fail counters for this run (the counters themselves live in
# _provenance-functions.ps1 so Write-Ok/Write-Fail can share them).
$script:Pass = 0
$script:Fail = 0

$CommonDotnetPaths = @(
  "agentweaver.sln"
  "global.json"
  "Directory.Build.props"
  "Directory.Packages.props"
  "NuGet.config"
  "packages"
)

Write-Host ""
Write-Host "=== Image provenance verification (against $VerifyGitRef = $($VerifyCommit.Substring(0,12))) ==="
Write-Host ""

# Get-DesiredDeploymentReplicas, Get-PodStatusLinesForSelector,
# Get-LiveDigestStateForSelector, and Invoke-VerifyImage are defined in
# _provenance-functions.ps1 (dot-sourced above).

Invoke-VerifyImage -Label "api"        -Image "agentweaver-api"        -PodSelector "app=agentweaver-api"      -ExpectedReplicas (Get-DesiredDeploymentReplicas "agentweaver-api")      -AllowEphemeralPods $false -Paths ($CommonDotnetPaths + @("apps/Agentweaver.Api"))      -VerifyCommit $VerifyCommit
Invoke-VerifyImage -Label "frontend"   -Image "agentweaver-frontend"   -PodSelector "app=agentweaver-frontend" -ExpectedReplicas (Get-DesiredDeploymentReplicas "agentweaver-frontend") -AllowEphemeralPods $false -Paths @("apps/web", "apps/Agentweaver.Web") -VerifyCommit $VerifyCommit
Invoke-VerifyImage -Label "mcp"        -Image "agentweaver-mcp"        -PodSelector "app=agentweaver-mcp"      -ExpectedReplicas (Get-DesiredDeploymentReplicas "agentweaver-mcp")      -AllowEphemeralPods $false -Paths ($CommonDotnetPaths + @("apps/Agentweaver.Mcp"))      -VerifyCommit $VerifyCommit
# AgentHost pods are pod-per-run sandboxes, not a Deployment. Claimed sandboxes
# from runs that started before a deploy can legitimately outlive the release
# and keep serving the older image until that run finishes. Provenance for the
# released AgentHost build should therefore verify the live warm-pool sandboxes
# sourced from the current SandboxTemplate, not every active claimed run pod.
Invoke-VerifyImage -Label "agent-host" -Image "agentweaver-agent-host" -PodSelector "app=agentweaver-sandbox,app.kubernetes.io/component=agent-host,agents.x-k8s.io/warm-pool-sandbox" -ExpectedReplicas "" -AllowEphemeralPods $true -Paths ($CommonDotnetPaths + @("apps/Agentweaver.AgentHost")) -VerifyCommit $VerifyCommit

Write-Host ""
Write-Host "==================================================="
Write-Host " PROVENANCE VERIFICATION SUMMARY: $($script:Pass) passed, $($script:Fail) failed"
Write-Host "==================================================="
if ($script:Fail -eq 0) {
  Write-Host " ALL IMAGES VERIFIED AGAINST SOURCE"
} else {
  Write-Host " SOME IMAGES FAILED PROVENANCE CHECK -- see output above"
}
Write-Host ""

if ($script:Fail -ne 0) { exit 1 }
exit 0
