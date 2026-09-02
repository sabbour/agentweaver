# Learnings


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
