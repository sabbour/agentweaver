
## [ERR-20260709-STEER1] unified-steering in-place revision terminal-event gap

**Logged**: 2026-07-09T04:57:00-07:00
**Priority**: critical
**Status**: in_progress
**Area**: backend

### Summary
Live staging proof (v0.9.12-rc1): assembly-gate in_place_steer resumed a subtask's child run, the revision agent turn ran and ended cleanly, but the run was marked failed with result 'watch_stream_completed_without_terminal_event' -> subtask ineligible -> assembly wedged -> terminal assembly_failed. Preview never reached.

### CONFIRMED ROOT CAUSE (2026-07-09, Morpheus, in code)
NOT a checkpoint-resume issue. in_place_steer calls StartRevisionAsync -> a FRESH RunStreamingAsync (isChild:true, IsRevision:true), not ResumeAsync. The coordinator CHILD pipeline is a TRIMMED graph `agent -> child-assemble-ready` with NO failure->terminal edge (RunWorkflowFactory.cs:761-767). Post-turn, AgentTurnExecutor.CommitChanges (LibGit2 `new Repository()`+commit) is the ONLY throwing op (GetDiff swallows errors, GetStepCount can't throw). On throw the old code emitted a "failed" WorkflowStep and RETHREW -> MAF ExecutorFailedEvent. RunWatchLoopService.WatchAsync only TERMINATES on a WorkflowOutputEvent; its ExecutorFailedEvent case emits a step but does NOT terminate -> child-assemble-ready never reached -> stream ends -> FailRunSafeAsync("watch_stream_completed_without_terminal_event"). The structural gap is the missing failure->terminal edge in the trimmed child graph.

### Error
child run ae2ae531 (subtask 14): status=failed result=watch_stream_completed_without_terminal_event
last events: tool.error(kill), agent.turn.usage(claude-sonnet-5, 38s, 2179 out tok), agent.turn.end(turnId 0)
coordinator run c19491ce: steering_decision=in_place_steer(effect_confirmed_applied) -> subtask.failed(14) -> assembly_blocked ineligible[14] -> assembly_failed

### Context
- The in-place revision DID resume (new turn executed) — NOT a reaped-pod/no-resume case.
- The orchestrator stream-watcher for the revision completed without observing a terminal subtask event (assemble_ready) for the revised subtask.
- ConsciousDispatchFreshFallbackAsync did NOT catch this runtime revision failure; it only guards the no-resumable-child-record case.

### Suggested Fix
Root-cause the seam between StartRevisionAsync (in-place resume) and RunOrchestrator terminal-event detection: ensure an in-place revision emits/propagates the terminal subtask event the watcher recognizes (mirror the fresh-dispatch terminal path). Additionally, a revision that ends with watch_stream_completed_without_terminal_event should trigger the conscious dispatch_fresh fallback (re-dispatch fresh pod, visible event) instead of failing the subtask and wedging assembly.

### Metadata
- Reproducible: yes (live proof)
- Related Files: apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs (ExecuteInPlaceSteerAsync, ConsciousDispatchFreshFallbackAsync), apps/Agentweaver.Api/Runs/RunOrchestrator.cs (revision watch/terminal detection), packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs (StreamTurnOnceAsync)
- See Also: agent streaming reliability memory (AsyncStreamIdleTimeout)
