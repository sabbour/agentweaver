# Isolate agent execution and workspaces

**Issue:** [#20](https://github.com/sabbour/agentweaver/issues/20)  
**Area:** Agent execution & sandbox

## User story

As a platform operator, I want agent work to run in isolated workspaces and sandboxes, so that model-written changes and commands cannot casually affect protected branches or unrelated server files.

## Context / problem

Agentweaver assumes generated changes are untrusted until reviewed. Isolation keeps edits, command execution, credentials, and failures inside a bounded run environment.

## Scope

### In
- per-run workspaces or worktrees
- sandbox policy for shell, isolation, and network posture
- pod-per-run execution where deployed
- safe release and recovery around suspended runs
- operator visibility into sandbox health

### Out
- bypassing repository allowlists
- sharing one mutable workspace across unrelated runs
- exposing arbitrary server paths to agents

## Acceptance criteria

- [ ] Agent edits occur in a run-specific workspace before merge.
- [ ] Sandbox policy can disable shell execution, isolation, or outbound access as configured.
- [ ] Cluster deployments can place agent turns in scoped sandbox pods.
- [ ] Suspended or failed runs do not leave users with hidden branch changes.
- [ ] Operators can see sandbox capacity and pod health where applicable.

## Notable edge cases

- Unavailable sandbox capacity leaves work queued or failed visibly.
- Local and cluster execution keep the same user-facing run model.
- Path and policy violations fail closed.
