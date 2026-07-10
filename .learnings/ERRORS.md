
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

## [ERR-20260709-PREVPORT] preview port_not_found (blind port discovery: no `ss`, buffered stdout)

**Logged**: 2026-07-09T20:05:00Z
**Priority**: high
**Status**: in_progress
**Area**: backend

### Summary
After the NetworkPolicy-preflight fix (v0.9.15), preview advanced past registration but failed with reason=port_not_found "preview-runner call failed: HTTP 500". Root cause: the AgentHost observe step could not DISCOVER the app's listening port at all.

### Evidence (run 4d74955a, staging)
- AppTraces: PreviewRunner started 19:50:30 -> process exited 19:51:31 exitCode=143 (SIGTERM) -> stopped reason=preview_step_failed:port_not_found. So the app did NOT crash; it ran ~61s then observe TIMED OUT and PreviewStep SIGTERM'd it.
- TimeoutException message: "Last health failure: none. Logs: > agentweaver-preview@1.0.0 start > node server.js" => ZERO candidate ports were ever probed.
- `kubectl exec` on agent-host pod: `ss`=MISSING (no /usr/bin/ss, no /bin/ss); /proc/net/tcp and /proc/net/tcp6 readable.
- Code: SnapshotListeningPortsAsync (PreviewRunner.cs ~608) returns [] when ss is absent; CandidatePortsFromLogs relies on stdout, but node block-buffers stdout when piped (not a TTY) so the "listening on 3000" line never flushed within the window.

### Root cause
Port discovery has two mechanisms and BOTH fail in the sandbox: (1) `ss -ltnp` socket diff -> ss binary not installed -> returns empty; (2) stdout log parse -> defeated by child process stdout buffering. Net: no candidate ports -> observe timeout -> bare HTTP 500 (opaque).

### Fix
PRIMARY: discover listening ports by reading /proc/net/tcp AND /proc/net/tcp6 (st==0A LISTEN, hex local port), namespace-local, no external binary. SECONDARY: observe must return a clean unhealthy observation with a precise reason (no_listening_port_discovered / process_exited) instead of throwing a 500, so preview_failed is legible. Keep log-parse as supplementary. Do NOT depend on adding `ss` to the image.

### Metadata
- Reproducible: yes (staging run 4d74955a)
- Related Files: apps/Agentweaver.AgentHost/PreviewRunner.cs (SnapshotListeningPortsAsync ~608, ObserveBoundPortAsync ~261), apps/Agentweaver.AgentHost/Program.cs (observe endpoint ~321 no try/catch)
- See Also: ERR-20260709-PREVNP (NetworkPolicy preflight, fixed v0.9.15)

## [ERR-20260709-PREVLIFE] preview killed by agenthost_shutdown (lifecycle coupled to run/pod)

**Logged**: 2026-07-09T20:55:00Z
**Priority**: high
**Status**: pending
**Area**: backend

### Summary
With v0.9.16 deployed (all warm pods on v0.9.16-rc1), the software-delivery preview proof (run fa1eaf28) reached assembly_complete but previewUrl was STILL empty. The /proc port-discovery fix was never exercised — the preview process was killed by pod shutdown before observe could run.

### Evidence (App Insights, run fa1eaf28)
- 20:39:39 PreviewRunner started (pid 140) — right as the assembly review gate opened (awaiting_review 20:39:41).
- 20:40:00 run status=completed result=assembly_complete (auto-approved by the proof).
- 20:40:03 PreviewRunner process exited exitCode=143 (SIGTERM); stopped reason=**agenthost_shutdown**.
- ZERO observe/health/candidate/proc traces in the 20:39:39–20:40:03 window → observe never got to run.
- kubectl events: agent-host pod 8bdqw (ran the final subtask) "Killing / Stopping container" at ~20:40 — matches run completion.
- /api/runs/{id}/sandbox/port-forward returns no sessions; run detail has no preview field.

### Root cause (5th preview layer)
The preview process runs inside the run's BOUND agent-host pod. That pod is released/stopped when the run (or its subtask) reaches terminal completion. So when the assembly review is approved and the run completes, the pod is torn down and the preview is SIGTERM'd (agenthost_shutdown) before it ever observes a port or registers a Service/HTTPRoute. The preview lifecycle is coupled to the ephemeral agent-host pod lifecycle. This is the "decouple preview" problem: preview must outlive run completion (or run must not release the pod while a preview is active, or preview must be hosted on a pod that persists).

### Secondary bug
The assembly/review approve endpoint returned HTTP 500: Npgsql DbUpdateConcurrencyException ("expected 1 row, affected 0") — optimistic-concurrency race between the approve POST and the coordinator's own assembly-completion write, across API replicas:2. The run completed anyway, but the approve API surfaced a 500 (should be idempotent/retry or return a clean 409/200).

### Suggested fix
Decouple the preview lifecycle from run/subtask completion: keep the preview session + its host pod alive until explicitly torn down (user closes preview / TTL), independent of run terminal status; provision the Service/HTTPRoute against a pod that persists. Also make the assembly/review approve resilient to the optimistic-concurrency race (retry-on-concurrency / treat already-completed as success).

### Metadata
- Reproducible: yes (staging run fa1eaf28)
- Related Files: apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs, apps/Agentweaver.AgentHost/PreviewRunner.cs, apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyReviewPersistence.cs, apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs
- See Also: ERR-20260709-PREVPORT (/proc discovery, fixed v0.9.16 but not yet proven), ERR-20260709-PREVNP (NetworkPolicy preflight, fixed v0.9.15)

## [ERR-20260709-PREVLIFE] preview-lifecycle coupled to run completion (durability gap)

**Logged**: 2026-07-09T14:20:00-07:00
**Priority**: high
**Status**: characterized (fix pending Ahmed direction)
**Area**: backend

### Summary
LIVE PROOF (v0.9.16-rc1, run fbf68ea4): preview mechanism WORKS end-to-end — reachable public
HTTPS URL serving the live running app THROUGHOUT the human-review window. But on approve/complete
the preview is destroyed (curl -> 404, port-forward sessions -> empty). Preview lifetime is coupled
to run completion.

### Root cause (confirmed in code)
CoordinatorAssemblyService.CleanupAssemblyBuildTestResourcesAsync (called on run completion) does BOTH:
  (a) StopPreviewsSafeAsync -> StopPreviewAsync deletes the HTTPRoute+Service, AND
  (b) CollectiveAssemblyPipeline.CleanupBuildTestResourcesAsync -> ReleaseAgentHostPodAsync deletes the pod.
PreviewReaper.Decide() reaps a preview as Orphan the instant podExists=false. The spec-006 durability
infra EXISTS (SandboxPreviewReaperService, IdleTimeoutMinutes=30, keepalive, MaxUntil) but is defeated
by completion-time force-teardown + pod release. NOTE: reaper StopPreviewAsync only deletes
HTTPRoute+Service, NOT the agent-host pod (pod has its own SandboxClaim TTL), so a durability fix that
keeps the pod alive must also ensure the pod is released when the preview idle-expires (else pod leak).

### Suggested Fix (needs Ahmed sign-off on resource tradeoff)
On completion, if a preview_ready is live for the workplan: SKIP StopPreviewsSafeAsync AND defer
ReleaseAgentHostPod; let the existing preview reaper own teardown at idle/max TTL, and have it release
the agent-host pod/SandboxClaim when it reaps. Tradeoff: a pod lingers up to IdleTimeoutMinutes per live
preview (capacity cost) — spec-006's 30-min TTL already accepted this, but confirm with Ahmed.

### Metadata
- Reproducible: yes (live proof fbf68ea4: 200 during review, 404 after approve)
- Related Files: apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs (CleanupAssemblyBuildTestResourcesAsync, StopPreviewsSafeAsync ~2652-2676), apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs (CleanupBuildTestResourcesAsync ~274), apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs (StopPreviewAsync/ReapAsync 422-490), apps/Agentweaver.Api/Sandbox/Preview/PreviewReaper.cs (Decide)
- See Also: ERR-20260709-PREVPORT

## [ERR-20260709-PREVPORT] RESOLVED — /proc port discovery (VERIFIED live)

**Status**: resolved (VERIFIED in live proof fbf68ea4, 2026-07-09T14:14)
### Resolution
v0.9.16-rc1 /proc/net/tcp{,6} port discovery + legible observe reasons: PROVEN working. Live run
fbf68ea4 emitted sandbox.preview_ready with target_port=8308 and a reachable URL; curl returned HTTP
200 serving the live app. The 4th blocker (blind port discovery) is fixed and confirmed end-to-end.

## [LRN-20260709-RDBLOCK] rubberduck gate can dead-end the happy path + contradicts platform port contract

**Logged**: 2026-07-09T14:20:00-07:00
**Priority**: high
**Status**: pending
**Area**: backend

### Summary
Two coupled issues surfaced while proving the preview:
1. The internal rubber-duck ASSEMBLY gate over-rejects a trivially-correct app (whack-a-mole nitpicks),
   and its #1 recurring complaint DIRECTLY CONTRADICTS the platform port contract: it flags
   `server.listen(process.env.PORT)` as a bug demanding `process.env.PORT || 3000`. The reviewer does
   not know the platform injects PORT. (`|| 3000` actually satisfies BOTH — platform always sets PORT.)
2. When in-place steering budget exhausts, RouteAssemblyGateThroughSteeringAsync's Proceed branch
   latches WorkPlanStatus.AssemblyBlocked (terminal dead-end) even though its own rationale says
   "escalate to human review / terminal". Run fbf68ea4-sibling 02e337e5 hung in assembly_blocked
   forever; the preview step never ran. This contradicts Ahmed's directive "missing preview shouldn't
   block human review" and "steering escalates to the coordinator/human".

### Suggested Action
- Fix B: steering_budget_exhausted should OPEN the human-review gate (awaiting_review) so a human can
  approve/reject despite reviewer nitpicks, NOT latch assembly_blocked. (CoordinatorAssemblyService.cs
  ~1810-1822; CoordinatorSteeringDecider.cs BuildRationale Proceed branch ~582.)
- Rubber-duck reviewer charter/prompt should know the platform injects PORT (so `process.env.PORT` alone
  is correct), to stop the contradictory rejection loop.
- (Proof-only mitigation already applied: preview-hold.ps1 goal uses `process.env.PORT || 3000`, explicit
  404, and a pure single unit test -> app now clears the rubber-duck gate first try.)

### Metadata
- Reproducible: yes (02e337e5 dead-ended assembly_blocked after 4 rejects; fbf68ea4 clean goal passed first try)
- Related Files: apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs (RouteAssemblyGateThroughSteeringAsync ~1737-1835), apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs (BuildRationale ~571-585)
