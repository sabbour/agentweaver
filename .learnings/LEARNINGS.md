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
