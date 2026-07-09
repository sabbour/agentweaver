
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

### Resolution
- **Resolved**: 2026-07-09T14:00:00Z
- **Commit**: 3fc7c93f (v0.9.13-rc1), deployed staging
- **Notes**: Live-proven on run d6f9b040 (project f4490ab1): software-delivery ran through 3 in-place-steer
  attempts across repeated rubberduck request-changes, converged, human-review approved, MERGED
  (commit cfa8948, assembly_complete). The exact wedge class (watch_stream_completed_without_terminal_event
  -> assembly_failed) NEVER occurred. Fix = AgentTurnExecutor transient-commit retry + visible rethrow;
  RunWatchLoopService child ExecutorFailedEvent terminalization; CoordinatorAssemblyService conscious+visible
  dispatch_fresh on in-place-no-terminal. All steering decisions were VISIBLE (no glitch).
- **Follow-up (NOT blocking, see LRN below)**: in-place revision produced a clean terminal only 1/3 times;
  the other 2 fell back to conscious dispatch_fresh (in_place_revision_no_terminal). Context-preservation
  works but is not yet the dominant path. Deeper in-place *resume* seam remains.

## [ERR-20260709-PREVNP] preview registration_failed (NetworkPolicy-blocked API preflight)

**Logged**: 2026-07-09T19:20:00Z
**Priority**: high
**Status**: in_progress
**Area**: backend

### Summary
Live preview still fails with reason=registration_failed "Nothing is listening on sandbox pod <pod> port 5431" even after the TCP forwarder fix. Port is now in-range (5431) and forwarder-bound 0.0.0.0, so de-hardcoding + forwarder worked. Root cause of THIS failure is a second, independent blocker.

### Error
```
sandbox.preview_failed reason=registration_failed
message="Nothing is listening on sandbox pod agentweaver-agent-host-xtqwt port 5431."
```

### Context
- PreviewStep start/observe/health resolve the BOUND agent-host pod origin; process+forwarder start there and observe verifies healthy THROUGH the forwarder (in-pod loopback). Good.
- SandboxPreviewService.StartPreviewAsync then calls EnsurePreviewTargetIsReachableAsync(podName,targetPort) BEFORE creating the Service/HTTPRoute. It does a DIRECT TcpClient.ConnectAsync(pod.Status.PodIP, targetPort) FROM THE API POD.
- k8s/networkpolicy-sandbox.yaml (sandbox-allow-preview-ingress) admits ports 3000-9000 on sandbox pods ONLY from pods labelled gateway.networking.k8s.io/gateway-name=agentweaver-preview-gateway. API pods are app=agentweaver-api -> NOT the gateway -> the preflight is denied by design and can NEVER pass. This blocks every preview before the route is even created.

### Suggested Fix
Remove/replace the API->pod direct preflight (architecturally invalid under sandbox isolation). Rely on the AgentHost forwarder-verified observe as the readiness signal, create the Service+HTTPRoute, return the preview_url. Optional: verify reachability THROUGH the gateway (the allowed path), non-fatal. Then curl the public URL in the proof for end-to-end confirmation.

### Metadata
- Reproducible: yes
- Related Files: apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs (EnsurePreviewTargetIsReachableAsync ~782-816, StartPreviewAsync ~137), k8s/networkpolicy-sandbox.yaml
- See Also: forwarder wave v0.9.14-rc1 (fixed loopback+hardcoded-3000)
