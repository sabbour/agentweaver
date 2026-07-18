# 25-verify-image-provenance.Tests.ps1 -- Regression coverage for #351.
#
# Guards against the #351 bug where AgentHost provenance verification checked
# EVERY live AgentHost pod, including claimed pre-deploy run sandboxes
# (ephemeral per-run pods that can legitimately keep running an older image
# after a release ships, until the run they belong to finishes). That caused
# spurious "mixed live digests" / stale-image failures against pods that were
# never part of the current release's warm pool in the first place.
#
# The fix scopes the AgentHost pod selector to warm-pool/deployment-template
# sandboxes only (an `agents.x-k8s.io/warm-pool-sandbox` label), excluding
# claimed run sandboxes. These tests simulate kubectl's server-side selector
# filtering via a mock of Get-PodStatusLinesForSelector, and assert
# verification only ever sees/uses the warm-pool pod's image tag/digest.
#
# Run with: Invoke-Pester (from a Pester 5+/6 install) against this file.

BeforeAll {
  . "$PSScriptRoot\..\_provenance-functions.ps1"

  # A warm-pool sandbox pod: part of the current SandboxTemplate/warm pool,
  # always in scope for provenance verification.
  $script:WarmPoolDigest = "sha256:$('a' * 64)"
  $script:WarmPoolLine = "agent-host-warmpool-0`tRunning`ttrue`tacr.example.io/agentweaver-agent-host:v2.0.0`tdocker-pullable://acr.example.io/agentweaver-agent-host@$script:WarmPoolDigest`t"

  # A claimed pre-deploy run sandbox: an ephemeral per-run pod that started
  # before the release and is legitimately still running the OLDER image.
  $script:ClaimedSandboxDigest = "sha256:$('b' * 64)"
  $script:ClaimedSandboxLine = "agent-host-run-abc123`tRunning`ttrue`tacr.example.io/agentweaver-agent-host:v1.9.0`tdocker-pullable://acr.example.io/agentweaver-agent-host@$script:ClaimedSandboxDigest`t"

  $script:WarmPoolSelector = "app=agentweaver-agent-host,app.kubernetes.io/component=agent-host,agents.x-k8s.io/warm-pool-sandbox"
  # The selector 25-verify-image-provenance.ps1 used BEFORE the #351 fix --
  # matches every live AgentHost pod, warm-pool or claimed-run alike.
  $script:PreFixBroadSelector = "app=agentweaver-agent-host,app.kubernetes.io/component=agent-host"
}

Describe "Get-LiveDigestStateForSelector (AgentHost warm-pool vs. claimed sandbox)" {

  BeforeEach {
    # Simulates kubectl's server-side `--selector` filtering: the warm-pool
    # selector only ever returns the warm-pool pod; the pre-fix broad selector
    # returns both the warm-pool pod AND the claimed run sandbox.
    Mock Get-PodStatusLinesForSelector {
      param([string]$Selector)
      if ($Selector -eq $script:WarmPoolSelector) {
        return @($script:WarmPoolLine)
      }
      return @($script:WarmPoolLine, $script:ClaimedSandboxLine)
    }
  }

  It "with the FIXED warm-pool-only selector, resolves cleanly to just the warm-pool pod's digest" {
    $state = Get-LiveDigestStateForSelector -Label "agent-host" -Selector $script:WarmPoolSelector -ExpectedReplicas "" -AllowEphemeralPods $true

    $state.Ok | Should -BeTrue
    $state.PodCount | Should -Be 1
    $state.Digest | Should -Be $script:WarmPoolDigest
    $state.Tag | Should -Be "v2.0.0"
  }

  It "with the PRE-FIX broad selector, a claimed run sandbox pollutes the check with a mixed-digest failure" {
    # This reproduces the #351 bug: including the claimed pre-deploy run
    # sandbox (still legitimately on the older image) alongside the warm-pool
    # pod causes a false "mixed live digests" failure, even though nothing is
    # actually wrong with the release.
    $script:Fail = 0
    $state = Get-LiveDigestStateForSelector -Label "agent-host" -Selector $script:PreFixBroadSelector -ExpectedReplicas "" -AllowEphemeralPods $true

    $state.Ok | Should -BeFalse
    $script:Fail | Should -BeGreaterThan 0
  }
}

Describe "Invoke-VerifyImage (AgentHost provenance, warm-pool scoped)" {

  BeforeEach {
    Mock Get-PodStatusLinesForSelector {
      param([string]$Selector)
      if ($Selector -eq $script:WarmPoolSelector) {
        return @($script:WarmPoolLine)
      }
      return @($script:WarmPoolLine, $script:ClaimedSandboxLine)
    }
    # Any live digest maps to a single resolvable provenance tag; Resolve-ProvenanceCommit
    # always resolves to $VerifyCommit itself, so `git diff --quiet` against it is
    # trivially empty regardless of which paths are being watched -- isolating
    # these tests from unrelated repo history.
    Mock Get-ProvenanceTagsForDigest { return @("prov-0000000000000000000000000000000000000000") }
    Mock Resolve-ProvenanceCommit { return (git rev-parse HEAD).Trim() }
  }

  It "verifies successfully using ONLY the warm-pool pod's image, ignoring the claimed sandbox" {
    $script:Pass = 0
    $script:Fail = 0
    $verifyCommit = (git rev-parse HEAD).Trim()

    Invoke-VerifyImage -Label "agent-host" -Image "agentweaver-agent-host" `
      -PodSelector $script:WarmPoolSelector -ExpectedReplicas "" -AllowEphemeralPods $true `
      -Paths @("apps/Agentweaver.AgentHost") -VerifyCommit $verifyCommit

    $script:Fail | Should -Be 0
    $script:Pass | Should -Be 1
    Should -Invoke Get-PodStatusLinesForSelector -Times 1 -ParameterFilter { $Selector -eq $script:WarmPoolSelector }
  }

  It "would have spuriously failed under the pre-#351-fix broad selector" {
    $script:Pass = 0
    $script:Fail = 0
    $verifyCommit = (git rev-parse HEAD).Trim()

    Invoke-VerifyImage -Label "agent-host" -Image "agentweaver-agent-host" `
      -PodSelector $script:PreFixBroadSelector -ExpectedReplicas "" -AllowEphemeralPods $true `
      -Paths @("apps/Agentweaver.AgentHost") -VerifyCommit $verifyCommit

    $script:Fail | Should -BeGreaterThan 0
  }
}

Describe "25-verify-image-provenance.ps1 selector definition" {
  It "scopes the AgentHost pod selector to warm-pool sandboxes, not every live AgentHost pod" {
    $scriptContent = Get-Content -Raw "$PSScriptRoot\..\25-verify-image-provenance.ps1"
    $scriptContent | Should -Match 'agents\.x-k8s\.io/warm-pool-sandbox'
  }

  # Regression for #352: AgentHost pods were relabeled from
  # `app=agentweaver-sandbox` to `app=agentweaver-agent-host`, but the local
  # verifier's selector was never updated, so it matched zero live pods and
  # silently skipped verification (AllowEphemeralPods=true treats "no pods
  # found" as an automatic pass). Assert the retired label can never
  # reappear, and that the current label is present.
  It "does NOT use the #352-retired 'app=agentweaver-sandbox' AgentHost selector" {
    $scriptContent = Get-Content -Raw "$PSScriptRoot\..\25-verify-image-provenance.ps1"
    $scriptContent | Should -Not -Match 'app=agentweaver-sandbox\b'
  }

  It "uses the current 'app=agentweaver-agent-host' AgentHost selector" {
    $scriptContent = Get-Content -Raw "$PSScriptRoot\..\25-verify-image-provenance.ps1"
    $scriptContent | Should -Match 'app=agentweaver-agent-host,app\.kubernetes\.io/component=agent-host'
  }
}

Describe "25-verify-image-provenance.sh selector definition" {
  # Regression for #352 (bash equivalent): same stale-label defect as the
  # PowerShell script above -- guard both implementations independently so
  # a future edit to only one of the two can't silently reintroduce it.
  It "does NOT use the #352-retired 'app=agentweaver-sandbox' AgentHost selector" {
    $scriptContent = Get-Content -Raw "$PSScriptRoot\..\25-verify-image-provenance.sh"
    $scriptContent | Should -Not -Match 'app=agentweaver-sandbox\b'
  }

  It "uses the current 'app=agentweaver-agent-host' AgentHost selector" {
    $scriptContent = Get-Content -Raw "$PSScriptRoot\..\25-verify-image-provenance.sh"
    $scriptContent | Should -Match 'app=agentweaver-agent-host,app\.kubernetes\.io/component=agent-host'
  }
}
