# _image-functions.ps1 -- Shared helper functions for 20-build-push-images.ps1.
#
# Split out of 20-build-push-images.ps1 so the SAME function bodies can be
# dot-sourced both by the main script (sequential planning/decision logic)
# and by the background `Start-Job` workers it launches to build/retag images
# in parallel (mirroring the bash version's backgrounded `&` subshells).
#
# Background jobs run in a separate PowerShell child process, so these
# functions read $env:DRY_RUN directly (inherited process environment)
# rather than relying on a script-scoped $IsDryRun variable that would not
# exist across the process boundary.
#
# Keep in sync with the corresponding functions inline in 20-build-push-images.sh
# (release_ref_for_tag, paths_changed, wait_for_acr_tag_digest, stamp_provenance,
# retag_image, build_image, current_deployment_tag, current_agenthost_tag).

function Get-CurrentDeploymentTag {
  param([string]$Deployment)
  if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) { return "" }
  $image = (kubectl get deployment $Deployment --namespace $env:NAMESPACE --output jsonpath='{.spec.template.spec.containers[0].image}' 2>$null)
  if ($LASTEXITCODE -ne 0 -or -not $image) { return "" }
  $segments = $image -split ':'
  if ($segments.Count -gt 1) { return $segments[-1] }
  return ""
}

function Get-CurrentAgentHostTag {
  if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) { return "" }
  $image = (kubectl get sandboxtemplate agentweaver-agent-host --namespace $env:NAMESPACE --output jsonpath='{.spec.podTemplate.spec.containers[0].image}' 2>$null)
  if ($LASTEXITCODE -ne 0 -or -not $image) { return "" }
  $segments = $image -split ':'
  if ($segments.Count -gt 1) { return $segments[-1] }
  return ""
}

# Resolves a release image tag to the commit which wrote that version to VERSION.
# Releases before v0.9.36 were tagged, but later deploys deliberately only updated VERSION.
# Looking up the version-bump commit preserves safe selective builds for both histories.
#
# Hardening (#251): more than one commit can end up writing the same VERSION
# value -- e.g. an out-of-band/poisoned build attempt that was superseded
# without a VERSION bump (see the v0.9.48-rc1 incident). If that happens we
# only trust the match when:
#   1) the repository is NOT shallow for the VERSION-history fallback, and
#   2) every commit that wrote this version is an ancestor of the selected
#      newest match (pairwise linear-history validation).
# If any match sits off that line we have no reliable way to know which commit
# actually produced the currently-deployed image, so we refuse to guess and
# return failure -- the caller (Test-PathsChanged/Invoke-ScheduleImage) treats an
# unresolved source commit as "changed" and takes the safe full-rebuild path
# instead of risking a stale retag-forward.
function Get-ReleaseRefForTag {
  param([string]$Tag)
  $version = $Tag -replace '^v', ''

  $resolved = (git rev-parse --verify "$Tag^{commit}" 2>$null)
  if ($LASTEXITCODE -eq 0 -and $resolved) { return $resolved.Trim() }

  $isShallow = (git rev-parse --is-shallow-repository 2>$null)
  if ($isShallow -eq "true") {
    Write-Warning "  [WARN] tag ${Tag}: repository is shallow; refusing VERSION-based source resolution (forcing rebuild)"
    return $null
  }

  $matches = @()
  $commits = (git log --format=%H --all -- VERSION 2>$null)
  foreach ($commit in $commits) {
    if (-not $commit) { continue }
    $shownVersion = ((git show "${commit}:VERSION" 2>$null) -join "") -replace '\s', ''
    if ($shownVersion -eq $version) { $matches += $commit }
  }

  if ($matches.Count -eq 0) { return $null }

  # git log --all lists newest-first, so matches[0] is the newest match. Every
  # other VERSION-writing commit must be an ancestor of that newest commit; if
  # any are not, VERSION history is ambiguous/diverged and we must rebuild.
  $newest = $matches[0]
  foreach ($candidate in $matches[1..($matches.Count - 1)]) {
    git merge-base --is-ancestor $candidate $newest 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "  [WARN] tag ${Tag}: multiple diverged commits wrote VERSION=${version}; refusing to guess source commit (forcing rebuild)"
      return $null
    }
  }

  return $newest
}

function Get-SourceCommitForTag {
  param([string]$Tag)
  if (-not $Tag) { return $null }
  return (Get-ReleaseRefForTag -Tag $Tag)
}

function Test-PathsChanged {
  param([string]$OldRef, [string]$NewRef, [string[]]$Paths)
  if (-not $OldRef -or -not $NewRef) { return $true }
  git diff --quiet $OldRef $NewRef -- @Paths 2>$null
  return ($LASTEXITCODE -ne 0)
}

function Wait-ForAcrTagDigest {
  param([string]$Image, [string]$Tag)
  for ($attempt = 1; $attempt -le 5; $attempt++) {
    $digest = (az acr repository show-manifests `
      --name $env:ACR_NAME `
      --repository $Image `
      --query "[?tags[?@=='$Tag']].digest" `
      --output tsv 2>$null)
    if ($digest) {
      return (($digest -split "`n")[0]).Trim()
    }
    Start-Sleep -Seconds 2
  }
  return $null
}

# Records, as an extra immutable ACR tag pointing at the same digest, which
# commit this image tag's content actually corresponds to (#251 ask #3:
# "stamp build SHA into image label"). This is deliberately independent of
# the build/retag decision above it, so a later, out-of-band check
# (25-verify-image-provenance.ps1) can answer "what commit does the image
# currently deployed actually correspond to?" without re-trusting whatever
# the build/retag decision decided at build time -- it protects against
# script bugs, manual 'az acr import' outside this script, or deploying an
# unexpected tag. Stamping is mandatory: shipping an image we cannot
# independently map back to source would recreate #251's blind spot, so any
# stamping failure fails the job. The prov tag uses the full 40-char commit
# SHA to avoid short-tag collisions.
function Invoke-StampProvenance {
  param([string]$Image, [string]$Tag, [string]$Commit)

  $isDryRun = ($env:DRY_RUN -eq "true")

  if (-not $Commit) {
    Write-Error "ERROR: no resolvable commit for ${Image}:${Tag}; refusing to ship unstamped image"
    return $false
  }
  $resolvedCommit = (git rev-parse --verify "$Commit^{commit}" 2>$null)
  if ($LASTEXITCODE -ne 0 -or -not $resolvedCommit) {
    Write-Error "ERROR: provenance commit '$Commit' for ${Image}:${Tag} is not resolvable in local git history"
    return $false
  }
  $resolvedCommit = $resolvedCommit.Trim()
  $provTag = "prov-$resolvedCommit"
  Write-Host "--- Stamping provenance ${Image}:${Tag} -> ${Image}:${provTag} ---"
  if ($isDryRun) {
    Write-Host "  [dry-run] Would run az acr import for ${Image}:${Tag} -> ${provTag}"
    return $true
  }

  $sourceDigest = Wait-ForAcrTagDigest -Image $Image -Tag $Tag
  if (-not $sourceDigest) {
    Write-Error "ERROR: source image ${Image}:${Tag} never resolved to a digest in ACR; refusing to stamp unverifiable provenance"
    return $false
  }

  az acr import `
    --name $env:ACR_NAME `
    --resource-group $env:RESOURCE_GROUP `
    --source "$($env:ACR_LOGIN_SERVER)/${Image}@${sourceDigest}" `
    --image "${Image}:${provTag}" `
    --force `
    --output none
  if ($LASTEXITCODE -ne 0) {
    Write-Error "ERROR: failed to stamp provenance tag ${Image}:${provTag} (az acr import exit $LASTEXITCODE); refusing to ship unstamped image"
    return $false
  }

  $stampedDigest = Wait-ForAcrTagDigest -Image $Image -Tag $provTag
  if (-not $stampedDigest) {
    Write-Error "ERROR: provenance tag ${Image}:${provTag} did not appear in ACR after import; refusing to ship unstamped image"
    return $false
  }
  if ($stampedDigest -ne $sourceDigest) {
    Write-Error "ERROR: provenance tag ${Image}:${provTag} resolved to $stampedDigest, expected $sourceDigest; refusing to ship mismatched provenance"
    return $false
  }

  Write-Host "  [prov]   $($env:ACR_LOGIN_SERVER)/${Image}:${provTag} (commit $resolvedCommit)"
  return $true
}

function Invoke-RetagImage {
  param([string]$Image, [string]$SourceTag, [string]$TargetTag)
  $isDryRun = ($env:DRY_RUN -eq "true")
  if ($SourceTag -eq $TargetTag) {
    Write-Host "  [skip]   ${Image}:${TargetTag} already points at the deployed tag"
    return $true
  }
  Write-Host "--- Retagging ${Image}:${SourceTag} -> ${Image}:${TargetTag} ---"
  if ($isDryRun) {
    Write-Host "  [dry-run] Would run az acr import for ${Image}:${SourceTag} -> ${TargetTag}"
    return $true
  }
  az acr import `
    --name $env:ACR_NAME `
    --resource-group $env:RESOURCE_GROUP `
    --source "$($env:ACR_LOGIN_SERVER)/${Image}:${SourceTag}" `
    --image "${Image}:${TargetTag}" `
    --force `
    --output none
  if ($LASTEXITCODE -ne 0) { return $false }
  Write-Host "  [retag]  $($env:ACR_LOGIN_SERVER)/${Image}:${TargetTag}"
  return $true
}

# Waits for a set of background image build/retag jobs (as scheduled by
# Invoke-ScheduleImage in 20-build-push-images.ps1) to finish, mirroring
# bash's `wait -n` + terminate_remaining_jobs(): report whichever job
# finishes FIRST, and if it failed, stop every other still-running job
# before reporting overall failure to the caller.
#
# IMPORTANT (#347-adjacent regression, see #351): job success/failure MUST be
# decided from the job's own $Job.State property ('Completed' vs 'Failed'),
# never from the content of its output/error streams. `az acr build` and other
# native commands routinely emit benign progress/warning text on stderr for a
# build that ultimately succeeds; PowerShell surfaces that text via
# Receive-Job's error stream/output even though the job's State is still
# 'Completed'. Treating any stderr-shaped text as failure caused sibling jobs
# to be aborted early and skipped provenance stamping for images that had, in
# fact, built successfully.
#
# Each entry in $Jobs is a [PSCustomObject]@{ Job = <job>; Name = <string> }.
# Returns $true if any job failed, $false if every job completed successfully.
function Wait-ForImageJobs {
  param([Parameter(Mandatory)][object[]]$Jobs)

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

  return $failed
}

function Invoke-BuildImage {
  param([string]$Image, [string]$Tag, [string]$Dockerfile, [string]$Commit)
  $isDryRun = ($env:DRY_RUN -eq "true")

  Write-Host "--- Building ${Image}:${Tag} (${Dockerfile}) ---"
  if ($isDryRun) {
    Write-Host "  [dry-run] Would run az acr build for ${Image}:${Tag}"
    if (-not (Invoke-StampProvenance -Image $Image -Tag $Tag -Commit $Commit)) { throw "stamp_provenance failed for ${Image}:${Tag}" }
    return
  }
  az acr build `
    --registry $env:ACR_NAME `
    --resource-group $env:RESOURCE_GROUP `
    --image "${Image}:${Tag}" `
    --file $Dockerfile `
    --output none `
    .
  if ($LASTEXITCODE -ne 0) { throw "az acr build failed for ${Image}:${Tag} (exit $LASTEXITCODE)" }
  Write-Host "  [built]  $($env:ACR_LOGIN_SERVER)/${Image}:${Tag}"
  # Also tag as latest-release so it always points at the most recently built version
  if (-not (Invoke-RetagImage -Image $Image -SourceTag $Tag -TargetTag "latest-release")) { throw "retag to latest-release failed for ${Image}:${Tag}" }
  if (-not (Invoke-StampProvenance -Image $Image -Tag $Tag -Commit $Commit)) { throw "stamp_provenance failed for ${Image}:${Tag}" }
}
