# Preview apps running inside a sandbox

**Issue:** [#21](https://github.com/sabbour/agentweaver/issues/21)  
**Area:** Agent execution & sandbox

## User story

As a reviewer, I want to open a live browser preview for a server started inside a run sandbox, so that I can inspect generated apps or APIs without weakening sandbox isolation.

## Context / problem

Agents often start development servers inside isolated environments. Browser preview provides a scoped, temporary route to the run’s own sandbox when cluster support is available.

## Scope

### In
- manual preview from active Kubernetes-backed runs
- agent-requested preview with human approval
- port selection within allowed range
- live preview URL and stop/expiry behavior
- run-scoped preview ownership

### Out
- preview for local non-pod runs
- permanent public exposure of sandbox services
- previewing another run’s pod

## Acceptance criteria

- [ ] Preview is offered only when the run has an eligible active sandbox.
- [ ] Users can choose a valid target port and start a preview session.
- [ ] The preview URL opens the server running in the run’s own sandbox.
- [ ] Users can stop a preview explicitly, and inactive previews expire automatically.
- [ ] Agent-initiated previews require approval unless explicitly auto-approved by policy.

## Notable edge cases

- Invalid ports are rejected.
- No preview URL case is explained clearly.
- A preview cannot be used to reach a different run’s sandbox.
