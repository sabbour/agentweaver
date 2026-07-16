# _provenance-functions.ps1 -- Shared helper functions for 25-verify-image-provenance.ps1.
#
# Split out of 25-verify-image-provenance.ps1 so these pure/kubectl-and-az-
# calling helpers can be dot-sourced and unit-tested (with Get-PodStatusLinesForSelector
# etc. mocked) in isolation from the top-level VERIFY_GIT_REF/CommonDotnetPaths
# orchestration in the main script.
#
# Keep in sync with the corresponding functions inline in 25-verify-image-provenance.sh.

$script:Pass = 0
$script:Fail = 0
function Write-Ok   { param([string]$Message) Write-Host "  [OK]   $Message"; $script:Pass++ }
function Write-Fail { param([string]$Message) Write-Host "  [FAIL] $Message" -ForegroundColor Red; $script:Fail++ }
function Write-Info { param([string]$Message) Write-Host "  [INFO] $Message" }

# Returns the desired replica count for a Deployment.
function Get-DesiredDeploymentReplicas {
  param([string]$Deployment)
  $result = (kubectl get deployment $Deployment --namespace $env:NAMESPACE --output jsonpath='{.spec.replicas}' 2>$null)
  if ($LASTEXITCODE -ne 0) { return "" }
  return $result
}

function Get-PodStatusLinesForSelector {
  param([string]$Selector)
  $result = (kubectl get pods `
    --namespace $env:NAMESPACE `
    --selector $Selector `
    --output jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.status.phase}{"\t"}{.status.containerStatuses[0].ready}{"\t"}{.status.containerStatuses[0].image}{"\t"}{.status.containerStatuses[0].imageID}{"\t"}{.metadata.deletionTimestamp}{"\n"}{end}' 2>$null)
  if ($LASTEXITCODE -ne 0) { return @() }
  return @($result -split "`n" | Where-Object { $_ })
}

function Get-ImageTagFromRef {
  param([string]$ImageRef)
  $lastSegment = $ImageRef.Substring($ImageRef.LastIndexOf('/') + 1)
  if ($lastSegment -match ':') {
    return $lastSegment.Substring($lastSegment.LastIndexOf(':') + 1)
  }
  return ""
}

function Get-ImageDigestFromId {
  param([string]$ImageId)
  if ($ImageId -match '(sha256:[0-9a-f]{64})') { return $Matches[1] }
  return ""
}

# Finds prov-<sha> tag(s) on the same repository whose digest matches the
# given live digest, and returns unique tag names.
function Get-ProvenanceTagsForDigest {
  param([string]$Image, [string]$Digest)
  $tags = (az acr repository show-manifests `
    --name $env:ACR_NAME `
    --repository $Image `
    --query "[?digest=='$Digest'].tags[]" `
    --output tsv 2>$null)
  if (-not $tags) { return @() }
  $flat = ($tags -split "[`t`n]") | Where-Object { $_ }
  return @($flat | Where-Object { $_ -match '^prov-[0-9a-f]{12}$' -or $_ -match '^prov-[0-9a-f]{40}$' } | Sort-Object -Unique)
}

function Resolve-ProvenanceCommit {
  param([string]$Commitish)
  $resolved = (git rev-parse --verify "$Commitish^{commit}" 2>$null)
  if ($LASTEXITCODE -eq 0 -and $resolved) { return $resolved.Trim() }
  $match = (git log --all --format=%H 2>$null | Where-Object { $_ -match "^$Commitish" } | Select-Object -First 1)
  return $match
}

# Determines the single live image digest/tag currently running for a pod
# selector.
#
# IMPORTANT (#351): $AllowEphemeralPods / $Selector for the AgentHost workload
# MUST be scoped to warm-pool/deployment-template sandbox pods only (e.g. via
# an `agents.x-k8s.io/warm-pool-sandbox` label selector), never to "every live
# AgentHost pod". Claimed pre-deploy run sandboxes are ephemeral per-run pods
# that can legitimately keep running an older image after a release ships
# (the run they belong to started before the new image was deployed), so
# including them here would fail provenance checks against an image version
# that isn't actually part of the current release's warm pool -- or worse,
# silently mix digests across an old claimed sandbox and a new warm-pool pod.
# This function itself is selector-agnostic; it is the CALLER's job (see
# Invoke-VerifyImage's -PodSelector for "agent-host" in the main script) to
# pass a selector that excludes claimed run sandboxes.
function Get-LiveDigestStateForSelector {
  param([string]$Label, [string]$Selector, [string]$ExpectedReplicas, [bool]$AllowEphemeralPods)

  $state = [PSCustomObject]@{ Digest = $null; Tag = $null; PodCount = 0; Ok = $false; Skipped = $false }

  if (-not $AllowEphemeralPods -and -not $ExpectedReplicas) {
    Write-Fail "${Label}: could not determine desired replica count for selector '$Selector'"
    return $state
  }

  $lines = Get-PodStatusLinesForSelector -Selector $Selector
  if ($lines.Count -eq 0) {
    if ($AllowEphemeralPods) {
      $state.Skipped = $true
      $state.Ok = $true
      return $state
    }
    Write-Fail "${Label}: no pods found for selector '$Selector'"
    return $state
  }

  $digest = $null
  $tag = $null
  $podCount = 0

  foreach ($line in $lines) {
    $parts = $line -split "`t"
    if ($parts.Count -lt 1 -or -not $parts[0]) { continue }
    $podName = $parts[0]
    $phase = if ($parts.Count -gt 1) { $parts[1] } else { "" }
    $ready = if ($parts.Count -gt 2) { $parts[2] } else { "" }
    $imageRef = if ($parts.Count -gt 3) { $parts[3] } else { "" }
    $imageId = if ($parts.Count -gt 4) { $parts[4] } else { "" }
    $deletionTimestamp = if ($parts.Count -gt 5) { $parts[5] } else { "" }

    if ($deletionTimestamp -or $phase -ne "Running") {
      if ($AllowEphemeralPods) {
        $podState = if ($deletionTimestamp) { "Terminating" } else { $phase }
        Write-Info "${Label}: ignoring pod $podName in state='$podState'"
        continue
      }
      Write-Fail "${Label}: pod $podName is phase='$phase' (expected Running); refusing provenance check while replicas are unavailable"
      return $state
    }
    $podCount++
    if ($ready -ne "true") {
      Write-Fail "${Label}: pod $podName is not Ready; refusing provenance check while replicas are unavailable"
      return $state
    }

    $podDigest = Get-ImageDigestFromId -ImageId $imageId
    if (-not $podDigest) {
      Write-Fail "${Label}: pod $podName has no resolvable imageID digest yet; refusing provenance check while replicas are unavailable"
      return $state
    }

    $podTag = Get-ImageTagFromRef -ImageRef $imageRef
    if (-not $digest) {
      $digest = $podDigest
      $tag = $podTag
      continue
    }

    if ($podDigest -ne $digest) {
      Write-Fail "${Label}: mixed live digests across replicas ($digest vs $podDigest); rollout/retag state is not uniform, refusing provenance check"
      return $state
    }
  }

  if ($podCount -eq 0) {
    if ($AllowEphemeralPods) {
      $state.Skipped = $true
      $state.Ok = $true
      return $state
    }
    Write-Fail "${Label}: no pods found for selector '$Selector'"
    return $state
  }
  if (-not $AllowEphemeralPods -and $podCount -ne [int]$ExpectedReplicas) {
    Write-Fail "${Label}: expected $ExpectedReplicas pod(s) for selector '$Selector', found $podCount; refusing provenance check while replicas are unavailable"
    return $state
  }

  $state.Digest = $digest
  $state.Tag = $tag
  $state.PodCount = $podCount
  $state.Ok = $true
  return $state
}

function Invoke-VerifyImage {
  param(
    [string]$Label,
    [string]$Image,
    [string]$PodSelector,
    [string]$ExpectedReplicas,
    [bool]$AllowEphemeralPods,
    [string[]]$Paths,
    [string]$VerifyCommit
  )

  $liveState = Get-LiveDigestStateForSelector -Label $Label -Selector $PodSelector -ExpectedReplicas $ExpectedReplicas -AllowEphemeralPods $AllowEphemeralPods
  if (-not $liveState.Ok) { return }
  if ($liveState.Skipped) {
    Write-Ok "${Label}: no Running pods found for selector '$PodSelector'; no ephemeral pod image to verify"
    return
  }
  if (-not $liveState.Digest) {
    Write-Fail "${Label}: could not determine live digest from running pods"
    return
  }

  $provTags = Get-ProvenanceTagsForDigest -Image $Image -Digest $liveState.Digest
  if ($provTags.Count -eq 0) {
    Write-Fail "${Label}: no prov-<sha> tag found for live digest $($liveState.Digest.Substring(0,19)) -- image predates the #251/#303 provenance fix, or was pushed by a route other than 20-build-push-images.ps1/.sh. Cannot verify provenance; treat as unverified, not passing."
    return
  }

  # An unchanged image can accumulate multiple prov-<sha> tags across successive
  # releases (each release's 'az acr import' retag stamps a fresh prov tag onto
  # the SAME already-existing digest, since the content genuinely didn't change).
  # That is not ambiguous -- all such tags describe bit-identical content. It is
  # sufficient for ANY one of the accumulated commits to show no drift in the
  # watched paths vs VerifyCommit; report which one, plus the ones we skipped.
  $resolvedOk = @()
  $resolvedStale = @()
  $resolvedUnresolvable = @()

  foreach ($provTag in $provTags) {
    $candidateCommit = Resolve-ProvenanceCommit -Commitish ($provTag -replace '^prov-', '')
    if (-not $candidateCommit) {
      $resolvedUnresolvable += $provTag
      continue
    }
    git diff --quiet $candidateCommit $VerifyCommit -- @Paths 2>$null
    if ($LASTEXITCODE -eq 0) {
      $resolvedOk += $candidateCommit
    } else {
      $resolvedStale += $candidateCommit
    }
  }

  if ($resolvedOk.Count -gt 0) {
    $resolvedCommit = $resolvedOk[0]
    $extraNote = ""
    if ($provTags.Count -gt 1) {
      $extraNote = " ($($provTags.Count) prov tags accumulated on this unchanged digest across releases; using $($resolvedCommit.Substring(0,12)))"
    }
    Write-Ok "${Label}: $($liveState.PodCount) live pod(s) run ${Image}:$(if ($liveState.Tag) { $liveState.Tag } else { '<digest-only>' }) at $($liveState.Digest.Substring(0,19)), provably built from $($resolvedCommit.Substring(0,12)); no drift in watched paths vs $($VerifyCommit.Substring(0,12))$extraNote"
    return
  }

  if ($resolvedStale.Count -gt 0) {
    Write-Fail "${Label}: $($liveState.PodCount) live pod(s) run ${Image}:$(if ($liveState.Tag) { $liveState.Tag } else { '<digest-only>' }) at $($liveState.Digest.Substring(0,19)), built from $($resolvedStale[0].Substring(0,12)), but watched paths changed since then vs $($VerifyCommit.Substring(0,12)) -- STALE IMAGE (this is exactly the #251 failure mode). Re-run scripts/aks/20-build-push-images.ps1 with -ForceRebuild for this image."
    return
  }

  Write-Fail "${Label}: none of the $($provTags.Count) prov tag(s) for live digest $($liveState.Digest.Substring(0,19)) resolve in local git history (shallow clone or rewritten history?): $($resolvedUnresolvable -join ', ')"
}
