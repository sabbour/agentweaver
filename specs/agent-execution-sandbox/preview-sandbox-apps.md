# Preview apps running inside a sandbox

**Issues:** [#21](https://github.com/sabbour/agentweaver/issues/21), [#615](https://github.com/sabbour/agentweaver/issues/615)  
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
- a first-class run lifecycle that keeps preview retention and cleanup policies aligned

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
- [ ] While any preview is active, its sandbox claim TTL is renewed, API/worker reapers defer
      deletion, active use reasserts retention, and the backing pod is protected from autoscaler
      eviction through one idempotent lifecycle transition.
- [ ] When the final preview stops or expires, the same lifecycle transition restores the normal
      claim TTL and eviction policy so sandbox cleanup remains bounded.
- [ ] Each project can configure the agent-preview approval window from 1 to 1440 minutes; new and
      existing projects safely default to 30 minutes.
- [ ] Pending preview approvals remain visible through an unresolved notification badge, a
      persistent toast, and an accessible run-timeline card that shows the expiry.
- [ ] After expiry, an owner can request a fresh approval attempt without restarting the run or
      executing the already-started preview process again.

## Notable edge cases

- Invalid ports are rejected.
- No preview URL case is explained clearly.
- A preview cannot be used to reach a different run’s sandbox.
- Stopping one of several previews for a run does not remove protection from the remaining previews.
- Retrying a non-expired, superseded, or terminal-run approval is rejected.
- Every retry gets a fresh request id; the expired request remains in the audit timeline.
