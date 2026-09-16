# Unified autonomous steering — Deep Dive

Unified autonomous steering replaces hidden gate-specific correction paths with one coordinator-owned routing mechanism. Assembly gate request-changes from human review, RAI, Rubberduck, and Build & Test normalize to a `SteeringSignal`. The envelope also accepts `agent`, `coordinator`, and `step` source values; schema support alone is not evidence that every source has a production ingress. In particular, agent-to-agent triggering is explicitly future-ready (`apps/Agentweaver.Api/Coordinator/SteeringSignal.cs:93–109`). The coordinator records a decision before its steering action executes.

For event payloads and routes, see the [reference](../reference/unified-steering.md). For the operator workflow, see the [experience guide](../experience/unified-steering.md).

## Mental model

The key invariant is visibility before effect. `CoordinatorSteeringService.SubmitSteeringAsync` persists and queues the signal, emits `coordinator.steering_received`, and does not execute recovery or reset any subtask. `CoordinatorSteeringDecider.DecideAsync` records the action and emits `coordinator.steering_decision` before in-place steering or fresh dispatch runs (`apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs:720–805`; `CoordinatorSteeringDecider.cs:105–272`).

There is no feature flag. Unified steering is the behavior in the assembly path.

## One signal shape

`SteeringSignal` is the normalized envelope for all correction feedback (`apps/Agentweaver.Api/Coordinator/SteeringSignal.cs:8`). It carries:

- `source`: `human-review`, `rai`, `rubberduck`, `build-test`, `agent`, `coordinator`, or `step`;
- `severity`: `advisory`, `request-changes`, or `blocking`;
- target scope: run, work plan, subtask ids, and optional child run id;
- feedback text;
- optional tree hash and explicit file hints.

Feedback text is reasoning context, not a routing heuristic. The old behavior that parsed prose and automatically reset subtasks is no longer the gate path.

## Coordinator choices

The coordinator chooses among four directions (`SteeringSignal.cs:129`):

| Direction | User-facing effect |
| --- | --- |
| `in_place_steer` | Resume the existing child run as a revision turn, preserving session, worktree, and context. |
| `dispatch_fresh` | Reset selected subtasks and launch fresh child runs. This is explicit, logged, and visible. |
| `proceed` | In the assembly path, open durable human review carrying the feedback; do not terminalize the plan merely because autonomous steering is exhausted. |
| `advisory` | Surface the signal and take no action. |

The shipped deterministic fallback policy prefers advisory no-op for advisory signals, proceeds when budgets are exhausted or feedback is blocking/stale, steers in place when the target is resumable, and dispatches fresh only when request-changes feedback targets a non-resumable path (`apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:31`).

### Resumability probe (pod-per-run vs in-api)

Whether a target is *resumable* is decided by an `ISteeringResumabilityProbe`. The in-api default checks durable child-run references and the retention window. `PodPerRunResumabilityProbe` additionally excludes successful terminal subtasks whose pods were released. Assembly enables this exclusion only for `IsPodPerRun && ReleasePodOnSuspend`; warm-pool configuration (`ReleasePodOnSuspend=false`) preserves the in-place path (`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2316–2319`; `tests/Agentweaver.Tests/Coordinator/UnifiedSteeringTests.cs:245–322`). A persisted `ChildRunId` alone is not proof that the remote session still exists.

A single-eligible-agent target does **not** necessarily escalate on its first rejection. If its session is resumable, the decider can keep it in place. If fresh dispatch is required and accumulated context is available, an otherwise deadlocked rotation degrades to bounded same-author fresh dispatch; a no-context deadlock or exhausted autonomous budget escalates to human review (`CoordinatorAssemblyService.cs:2349–2388`; `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs:949`, `:1020`).

## Assembly gate integration

Assembly gate request-changes no longer calls a separate automatic reset route. `RouteAssemblyGateThroughSteeringAsync` stamps `assembly_steering`, submits the signal, claims it for the inline decider, and executes the chosen direction (`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2231–2399`).

- **A: in-place steer.** Mark the directive executing, resume the existing child session/worktree, and confirm the attempt-specific revision effect before settling it (`CoordinatorAssemblyService.cs:2333–2346`).
- **B: dispatch fresh.** Only after a decision event, execute the explicit rotation/reset path, preserving accumulated feedback; dependencies are rebuilt without applying author lockout to unaffected authors (`CoordinatorAssemblyService.cs:2349–2369`).
- **C: proceed.** Mark the directive executing, then open durable human review and settle the escalation effect only once that gate is durably open. Budget exhaustion is not terminal `assembly_blocked` (`CoordinatorAssemblyService.cs:2372–2388`, `:2845–2991`).
- **D: advisory.** Restore the assembly stage, mark the directive applied, and continue the gate loop (`CoordinatorAssemblyService.cs:2392–2397`).

Collective RAI RED is a separate safety escalation into the same durable human-review boundary, with reason `rai_red`, rather than terminal `RaiBlocked` (`CoordinatorAssemblyService.cs:3752–3795`). It is not an autonomous request-changes retry.

The assembly decision lease is recoverable. `AssemblySteering` routes back to assembly recovery rather than dispatch, and the reconciler scans it as an assembly state (`apps/Agentweaver.Api/Coordinator/CoordinatorRecoveryRouter.cs:55`, `apps/Agentweaver.Api/Coordinator/CoordinatorReconciler.cs:128`).

## Failure recovery & reliability

In-place steering resumes the same child run and worktree, returns the plan to dispatching, and re-arms assembly after that child reaches `assemble_ready`. This is the context-preserving path: it does not substitute an unannounced fresh child run.

The executor retries transient post-turn commit failures with bounded backoff. Production root and child graphs enable typed terminal-failure output: after persistent commit/publication failure, the executor emits a failed step and returns `AgentTurnOutput.TerminalFailureReason` (for example `commit_failed_persistent`) instead of pretending the revision produced no changes or relying on a bare rethrow (`packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:163–244`). The child graph routes that result to `child-turn-failed`; a clean result reaches `child-assemble-ready` (`apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:803–814`). Unexpected unhandled executor exceptions still use the watch-loop failure backstop.

A failed in-place revision does not silently wedge assembly or drop feedback. `DriveOutstandingSteeringExecutionAsync` detects failed target subtasks and records a visible fallback decision before fresh work is dispatched. The original steering instruction is preserved.

Crash-window hardening is strict: an in-place directive is marked `applied` only after every target subtask is assembly-eligible (`assemble_ready` or `completed`) **and** every target child has a confirmed `SteeringRevisionExecution` effect marker for that directive attempt. A crash before the revision launches leaves no confirmed marker, so recovery re-drives the missing child instead of silently dropping the feedback (`tests/Agentweaver.Tests/Coordinator/UnifiedSteeringTests.cs:578–643`).

## Loop bounds and idempotency

Steering cannot loop forever:

- per-subtask recovery attempts are capped at `CoordinatorSteeringService.MaxRecoveryAttempts = 3`;
- per-plan steering iterations default to `6`.

The decider increments the budget exactly once for in-place or fresh-dispatch actions and degrades to `proceed` when a cap is reached (`CoordinatorSteeringDecider.cs:164–247`). In-place steering uses attempt-specific revision effect records, so recovery can prove whether the revision actually ran before re-driving or marking the directive applied.

These caps bound **autonomous** convergence, not human participation. A human request-changes unconditionally resets the plan steering budget and the full affected redispatch closure's recovery attempts; the human round-trip counter is telemetry, not a cap. Autonomous gate sources cannot reset their own budgets (`CoordinatorAssemblyService.cs:2264–2287`; `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs:467`, `:512`).

## Source

| Concern | File |
| --- | --- |
| Steering signal schema and directions | `apps/Agentweaver.Api/Coordinator/SteeringSignal.cs` |
| Unified steering submission and received event | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs` |
| Coordinator decision policy, budgets, decision event | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs` |
| Assembly gate steering-only routing | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| Recovery routing for `assembly_steering` | `apps/Agentweaver.Api/Coordinator/CoordinatorRecoveryRouter.cs`, `CoordinatorReconciler.cs` |
| Event type constants | `packages/Agentweaver.Domain/EventTypes.cs` |
| Web run projection | `apps/web/src/timeline/runTimelineSteps.ts`, `apps/web/src/components/AgentSessionPanel.tsx` |

## See also

- [Unified autonomous steering — Reference](../reference/unified-steering.md)
- [Unified autonomous steering — User Guide](../experience/unified-steering.md)
- [Resilient assembly-review loop — Deep Dive](./resilient-assembly-review.md) — how budget exhaustion escalates to human review, context-preserving revisions, and the reviewer-rejection lockout built on top of unified steering.
- [Coordinator internals](./coordinator-internals.md)
- [Events & observability](./events-observability.md)

<details id="diagram-context-unified-steering-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>One signal, four explicit effects</td></tr>
<tr><td>takeaway</td><td>Durable decisions choose resume, fresh dispatch, human escalation or advisory continuation.</td></tr>
<tr><td>group-title-0</td><td>NORMALIZED FEEDBACK AND DURABLE DIRECTIVE</td></tr>
<tr><td>group-title-1</td><td>DECISION AND RESUMABILITY</td></tr>
<tr><td>group-title-2</td><td>ALTERNATIVE EFFECTS</td></tr>
<tr><td>Gate feedback</td><td>Gate feedback</td></tr>
<tr><td>Gate feedback</td><td>Implemented assembly sources</td></tr>
<tr><td>Gate feedback</td><td>structured scope</td></tr>
<tr><td>SteeringSignal</td><td>SteeringSignal</td></tr>
<tr><td>SteeringSignal</td><td>Normalize reason and targets</td></tr>
<tr><td>SteeringSignal</td><td>one decision contract</td></tr>
<tr><td>Persist directive</td><td>Persist directive</td></tr>
<tr><td>Persist directive</td><td>Received event is visible</td></tr>
<tr><td>Persist directive</td><td>queued / durable</td></tr>
<tr><td>Bounded decider</td><td>Bounded decider</td></tr>
<tr><td>Bounded decider</td><td>Budget + attempt resumability</td></tr>
<tr><td>Bounded decider</td><td>human-only budget reset</td></tr>
<tr><td>Persist decision</td><td>Persist decision</td></tr>
<tr><td>Persist decision</td><td>Decision event follows commit</td></tr>
<tr><td>Persist decision</td><td>explicit direction</td></tr>
<tr><td>In-place steer</td><td>In-place steer</td></tr>
<tr><td>In-place steer</td><td>Keep author and session</td></tr>
<tr><td>In-place steer</td><td>attempt-specific proof</td></tr>
<tr><td>Fresh dispatch</td><td>Fresh dispatch</td></tr>
<tr><td>Fresh dispatch</td><td>Conscious fresh execution</td></tr>
<tr><td>Fresh dispatch</td><td>bounded same-author fallback</td></tr>
<tr><td>Durable human park</td><td>Durable human park</td></tr>
<tr><td>Durable human park</td><td>Proceed / exhausted budget</td></tr>
<tr><td>Durable human park</td><td>not a failure terminal</td></tr>
<tr><td>Advisory continuation</td><td>Advisory continuation</td></tr>
<tr><td>Advisory continuation</td><td>No child-state reset</td></tr>
<tr><td>Advisory continuation</td><td>separate from in-place</td></tr>
<tr><td>e0</td><td>normalize</td></tr>
<tr><td>e1</td><td>submit</td></tr>
<tr><td>e2</td><td>decide</td></tr>
<tr><td>e3</td><td>commit</td></tr>
<tr><td>e4</td><td>resume</td></tr>
<tr><td>e5</td><td>fresh</td></tr>
<tr><td>e6</td><td>Proceed</td></tr>
<tr><td>e7</td><td>advisory</td></tr>
<tr><td>groups</td><td>NORMALIZED FEEDBACK AND DURABLE DIRECTIVE; DECISION AND RESUMABILITY; ALTERNATIVE EFFECTS</td></tr>
</tbody></table>
</details>
