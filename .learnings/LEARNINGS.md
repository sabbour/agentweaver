# Learnings

## [LRN-20260916-RELEASE-NOTE-GRANULARITY] correction

**Logged**: 2026-09-16T01:55:00-07:00
**Priority**: high
**Status**: resolved
**Area**: docs

### Summary
Large integration PRs need separate changeset fragments for distinct user-facing fixes.

### Details
One combined fragment obscured five independently meaningful changes: managed Preview durability,
provider-consistent run resume, trace/topology observability, Azure deployment reliability, and UI
harness wide-layout validation.

### Suggested Action
Before release preparation, compare the merged PR's behavioral surfaces with its pending changesets
and split unrelated release-note concerns even when they all produce one patch release.

### Metadata
- Source: user_feedback
- Related Files: .changeset/
- Tags: release, changesets, release-notes

### Resolution
- **Resolved**: 2026-09-16T01:55:00-07:00
- **Notes**: Replaced the combined fragment with five concern-specific changesets before recutting v0.32.7.

---

## [LRN-20260915-RELEASE-FORWARD-PORT] correction

**Logged**: 2026-09-15T01:25:24-07:00
**Priority**: critical
**Status**: promoted
**Area**: infra

### Summary
Every published release preparation must be forward-ported to `dev` before planning the next release.

### Details
The release workflow repeatedly omitted `release:sync-dev`. Prior sessions show
the omission caused `dev`/`main` divergence across v0.18.x, and the current
release attempt planned v0.32.5 even though that tag was already published.
Documentation alone did not prevent recurrence.

### Suggested Action
Enforce the invariant in `release:plan` and `release:prepare` by comparing the
repository version with published semver tags and failing with explicit
`release:sync-dev` guidance when `dev` is stale.

### Metadata
- Source: user_feedback
- Related Files: scripts/changesets/plan-release.mjs, scripts/changesets/prepare-release.mjs, RELEASING.md, AGENTS.md
- Tags: release, changesets, sync-dev, forward-port, automation
- Promoted: AGENTS.md, RELEASING.md, executable release guards
- Pattern-Key: release.forward_port_preparation
- Recurrence-Count: 3
- First-Seen: 2026-08-24
- Last-Seen: 2026-09-15

---

## [LRN-20260709-A1] knowledge_gap

**Logged**: 2026-07-09T14:00:00Z
**Priority**: medium
**Status**: pending
**Area**: backend

### Summary
Unified in-place steering revision reaches a clean assemble_ready terminal only sometimes; when the revised
child ends without a terminal it consciously (and visibly) falls back to dispatch_fresh.

### Details
Live run d6f9b040 (v0.9.13-rc1): 3 in_place_steer attempts -> 1 effect_confirmed_applied (true context
preservation), 2 in_place_revision_no_terminal -> conscious dispatch_fresh. The v0.9.13 fix (AgentTurnExecutor
transient-commit retry + RunWatchLoopService child ExecutorFailedEvent terminalization) eliminated the WEDGE
and made every steering decision visible, but the in-place *resume* path still frequently ends without emitting
the assemble_ready terminal the coordinator waits for -> so context is dropped (fresh dispatch) 2/3 of the time.
This is acceptable per Ahmed (conscious + visible fresh dispatch is fine; the invisible glitch/wedge was the bug),
but the desired end-state is in-place recovery as the DOMINANT path to truly preserve child worktree/session.

### Suggested Action
Root-cause why the in-place revision resume ends without a clean terminal (distinct from the child-executor-failure
path already terminalized). Likely the revision resume uses a different MAF resume/emit path than a fresh child turn;
trace CoordinatorAssemblyService.ExecuteInPlaceSteerAsync -> StartRevisionAsync -> child terminal emission and ensure
the revised turn emits the same assemble_ready terminal a first-time dispatch does.

### Metadata
- Source: live proof (run d6f9b040)
- Related Files: apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs, packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs, apps/Agentweaver.Api/Runs/RunWatchLoopService.cs
- See Also: ERR-20260709-STEER1
- Tags: steering, in-place-revision, assemble-ready-terminal

---

## [LRN-20260902-001] correction

**Logged**: 2026-09-02T03:14:00-07:00
**Priority**: medium
**Status**: pending
**Area**: frontend

### Summary
Do not plan local Agentweaver browser validation when the Entra ID app redirect cannot return to localhost.

### Details
The user clarified that the local UI cannot complete authentication because the configured Entra application redirect targets the deployed environment. Starting the local API/web stack and creating an empty browser storage state cannot make the authenticated workflow pages testable.

### Suggested Action
Use deterministic component/layout tests locally. Perform browser evidence capture only against an authorized deployment that contains the change and has valid managed Edge authentication.

### Metadata
- Source: user_feedback
- Related Files: scripts/ui-harness/SKILL.md
- Tags: entra, local-development, ui-harness, authentication

---

## [LRN-20260716-001] correction

**Logged**: 2026-07-16T14:04:04-07:00
**Priority**: medium
**Status**: pending
**Area**: infra

### Summary
Do not infer that an in-progress deployment is GitHub Actions when another local agent may be deploying directly.

### Details
The user clarified that the competing deployment was launched by another local agent, not GHA. GitHub workflow status was therefore not an authoritative completion signal.

### Suggested Action
Before a release, identify the deployment source. For local-agent deployments, gate on AKS rollout/image stability and refresh `origin/main` immediately before integration instead of relying on GHA status.

### Metadata
- Source: user_feedback
- Related Files: scripts/aks/
- Tags: deployment, local-agent, github-actions, aks, release-safety

---

## [LRN-20260710-001] correction

**Logged**: 2026-07-10T05:18:22-07:00
**Priority**: high
**Status**: pending
**Area**: infra

### Summary
“Agentweaver API only” excludes the specialized `agentweaver` agent and Agentweaver MCP orchestration tools.

### Details
The coordinator misread an API-only validation objective as permission to launch the specialized `agentweaver` custom agent and may prematurely treat missing harness credentials as a product defect.

### Suggested Action
Dispatch a normal Squad agent that calls the public HTTP API directly. Before declaring authentication blocked, use the current user's existing GitHub OAuth token when explicitly authorized; keep it memory-only and never print or persist it. Treat missing credential setup as harness preflight, not a product defect.

### Metadata
- Source: user_feedback
- Related Files: .learnings/LEARNINGS.md
- Tags: agentweaver, api-only, squad, delegation, authentication, harness-preflight

---
