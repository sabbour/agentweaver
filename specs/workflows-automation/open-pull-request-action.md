# Open a pull request as a workflow action

**Issue:** _to be assigned_  
**Area:** Workflows & automation

## User story

As a project owner, I want a workflow to include an action that opens a pull request on the connected repository, so that work produced by automation lands as a reviewable pull request without a manual follow-up step.

## Context / problem

Workflows drive how Agentweaver runs work, but completing that work as a pull request on the connected repository is still a manual step outside the pipeline. Making pull request creation an automatable workflow action closes the loop: after a run produces changes on a branch, the workflow itself can surface them for review on the repository.

## Scope

### In
- a workflow action that opens a pull request on the project's connected repository
- specifying the pull request title, body, base branch, head branch, and draft state
- using the connected repository and the acting user's authorization
- exposing the created pull request so later steps and history can reference it

### Out
- merging or auto-approving the pull request
- managing reviewers, labels, or assignees on the pull request
- opening pull requests on repositories other than the connected one
- replacing the existing run review and merge behavior

## Acceptance criteria

- [ ] A workflow can include an action that opens a pull request on the connected repository.
- [ ] The action accepts a title, body, base branch, head branch, and draft state, with safe defaults.
- [ ] Running the action creates the pull request on the project's connected repository.
- [ ] The created pull request is referenceable by later steps and visible in run history.
- [ ] Predictable failures are reported without aborting the rest of the run.

## Notable edge cases

- An action with no pushed head branch or no changes is reported clearly rather than failing silently.
- Attempting to open a pull request that already exists is handled without creating a duplicate.
- The action never targets a repository other than the project's connected one.
