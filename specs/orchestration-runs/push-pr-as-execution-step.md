# Push a pull request as a coordinator execution step

**Issue:** [#394](https://github.com/sabbour/agentweaver/issues/394)  
**Area:** Orchestration & runs

## User story

As a project owner, I want a coordinator run to publish its assembled branch as a pull request only after its upstream gates pass, so that the connected repository receives one reviewable PR without a separate manual push step.

## Context / problem

Coordinator assembly already models downstream quality and safety gates such as Build & Test, RAI, and Human Review in the run graph. Publishing the run's branch to GitHub is still outside that first-class pipeline, which forces a manual handoff even when the run has already reached the point where it is safe to present the result upstream.

This is distinct from [Open a pull request as a workflow action](../workflows-automation/open-pull-request-action.md) ([#49](https://github.com/sabbour/agentweaver/issues/49)). That spec covers a reusable workflow-authored action that a workflow designer can place in a custom automation. This spec instead defines a platform-owned coordinator assembly step: it is part of the built-in run/assembly execution graph, runs only after its upstream assembly gates have passed, and publishes the coordinator run's own branch on the connected repository.

## Scope

### In
- a first-class coordinator assembly step that runs after configured upstream gates such as Build & Test, RAI, and Human Review have passed
- pushing the coordinator run's branch to the connected repository using the acting user's authorization
- opening a new pull request for that branch on the connected repository when no open pull request exists yet
- detecting an existing open pull request for the same branch and updating it by pushing new commits instead of creating a duplicate
- recording the resulting pull request reference so run history and the coordinator graph can show the publication outcome
- reporting when there is no publishable change after gating, without pretending a pull request was opened

### Out
- replacing [Open a pull request as a workflow action](../workflows-automation/open-pull-request-action.md); the two surfaces may share underlying push / PR-creation logic in implementation but remain separate product behaviors
- merging or auto-approving the pull request
- managing reviewers, labels, assignees, or other pull request metadata beyond the minimum needed to open or update it
- opening pull requests on repositories other than the project's connected repository
- bypassing or overriding failed, reversed, or incomplete upstream gate results

## Acceptance criteria

- [ ] The coordinator assembly graph can include a dedicated pull-request publication step as a downstream execution stage.
- [ ] The step only runs after its configured upstream assembly gates have passed.
- [ ] Running the step pushes the coordinator run's branch to the connected repository using the acting user's authorization.
- [ ] If no open pull request exists for that branch, the step opens a new pull request on the connected repository.
- [ ] If an open pull request already exists for that branch, the step reuses it and updates it by pushing new commits instead of creating a duplicate pull request.
- [ ] The resulting pull request reference or no-change outcome is visible in run history and on the coordinator execution path.
- [ ] Predictable publication failures are surfaced clearly without silently overriding gate state or pretending publication succeeded.

## Notable edge cases

- If the assembled branch has no publishable diff after upstream gates pass, the step reports a no-change publication outcome instead of opening an empty pull request.
- If a prior pull request for the branch is already closed or merged, the step does not treat it as the reusable open pull request and instead handles the next publication attempt explicitly rather than silently updating the historical PR.
- If the branch tip no longer matches the run's expected assembled tree, publication is blocked or retried explicitly rather than trusting a best-effort diff string alone.
- If a manual push or another publisher races on the same branch, the step detects the mismatch and resolves it predictably instead of creating duplicate pull requests or publishing the wrong commit.
- If an upstream gate result is later reversed after publication already ran, the run history stays auditable rather than implying the pull request remained continuously authorized.
