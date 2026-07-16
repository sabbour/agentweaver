# 20-build-push-images.Tests.ps1 -- Regression coverage for #351.
#
# Guards against the #351 bug where Wait-ForImageJobs (formerly inlined in
# 20-build-push-images.ps1) decided job success/failure from the CONTENT of a
# job's output/error stream instead of its actual $Job.State. `az acr build`
# and other native commands routinely emit benign stderr-shaped text for a job
# that still completes successfully; treating that text as failure caused
# sibling build jobs to be stopped early and skipped provenance stamping for
# images that had, in fact, built fine.
#
# Run with: Invoke-Pester (from a Pester 5+/6 install) against this file.

BeforeAll {
  . "$PSScriptRoot\..\_image-functions.ps1"
}

Describe "Wait-ForImageJobs" {

  It "does NOT treat a completed job with stderr-shaped output as a failure" {
    # Simulates a real 'az acr build' run that succeeds (exit 0) but writes
    # warning-looking text to stderr along the way -- exactly the shape of
    # output that triggered the #351 regression.
    $job = Start-Job -Name "stderr-warning-job" -ScriptBlock {
      cmd /c "echo WARNING: az acr build noticed a slow layer 1>&2"
      exit 0
    }

    try {
      $jobs = @([PSCustomObject]@{ Job = $job; Name = "agentweaver-api:v1.2.3" })
      $failed = Wait-ForImageJobs -Jobs $jobs

      $job.State | Should -Be 'Completed'
      $failed | Should -BeFalse
    } finally {
      Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
  }

  It "treats a genuinely failed job's State as a failure and stops sibling jobs" {
    # A job whose script block throws (mirroring Invoke-BuildImage throwing on
    # a real az acr build non-zero exit code) must land in State='Failed'.
    $failJob = Start-Job -Name "real-failure-job" -ScriptBlock {
      cmd /c "exit 1"
      if ($LASTEXITCODE -ne 0) { throw "az acr build failed for agentweaver-mcp:v1.2.3 (exit 1)" }
    }
    # A slow sibling that would still be Running when the failure is observed;
    # Wait-ForImageJobs must Stop-Job it rather than let it run to completion.
    $siblingJob = Start-Job -Name "sibling-job" -ScriptBlock {
      Start-Sleep -Seconds 60
    }

    try {
      $jobs = @(
        [PSCustomObject]@{ Job = $failJob; Name = "agentweaver-mcp:v1.2.3" },
        [PSCustomObject]@{ Job = $siblingJob; Name = "agentweaver-frontend:v1.2.3" }
      )

      $failed = Wait-ForImageJobs -Jobs $jobs

      $failJob.State | Should -Be 'Failed'
      $failed | Should -BeTrue
      # The sibling must have been signalled to stop, not left running.
      $siblingJob.State | Should -Not -Be 'Running'
    } finally {
      Remove-Job -Job $failJob -Force -ErrorAction SilentlyContinue
      Remove-Job -Job $siblingJob -Force -ErrorAction SilentlyContinue
    }
  }
}
