# Unified autonomous steering — Reference

See [Bounded autonomy, same-author context fallback and human escalation](../diagrams/resilient-assembly-review-fig1.png) for the shared visual model.

See [Persisted feedback, explicit decision and confirmed effect](../diagrams/unified-steering-fig1.png) for the shared visual model.

Reference for the coordinator-owned steering path. Every correction signal is persisted, surfaced, decided by the coordinator, and then executed according to that decision.

For the implementation flow, see the [deep dive](../deep-dive/unified-steering.md). For operator behavior, see the [experience guide](../experience/unified-steering.md).

## Routes

| Method & path | Body | Returns | Notes |
| --- | --- | --- | --- |
| `POST /api/runs/{coordinatorRunId}/steer` | `kind`, optional `target_child_run_id`, `instruction` | Steering directive view | Human steering entry point. `pause` is not supported. |
| `POST /api/runs/{coordinatorRunId}/assembly/review` | `approved`, `request_changes`, `declined`, `feedback`, optional target files | Assembly review decision | Human review still posts to the review gate, but correction feedback is routed through unified steering. |

The separate Assembly Gate route was removed; correction feedback uses unified steering through the coordinator.

## Steering signal fields

`SteeringSignal` is the internal normalized contract (`apps/Agentweaver.Api/Coordinator/SteeringSignal.cs:29`).

| Field | Values / type | Meaning |
| --- | --- | --- |
| `CoordinatorRunId` | string | Coordinator run that owns the decision. |
| `Source` | `human-review`, `rai`, `rubberduck`, `build-test`, `agent`, `coordinator`, `step` | Where the feedback came from. |
| `TargetScope` | `{ kind, subtaskIds?, childRunId? }` | Run, work-plan, or subtask target. |
| `Feedback` | string | Reasoning context for the coordinator; not parsed for hidden routing. |
| `Severity` | `advisory`, `request-changes`, `blocking` | How strong the signal is. |
| `Verb` | `stop`, `send`, `redirect`, `amend`, `dispatch-fresh` | Delivery verb. |
| `TreeHash` | string or null | Aggregate tree hash the feedback was produced against. |
| `TargetFiles` | string array or null | Explicit hints only; never inferred from prose. |
| `CreatedBy` | string | User, agent, or gate id. |

## Decision directions

| Decision | Meaning | Effect |
| --- | --- | --- |
| `in_place_steer` | A: context-preserving correction | Resume the same child run/session/worktree with revision feedback. |
| `dispatch_fresh` | B: conscious fresh dispatch | Reset selected subtasks and launch fresh child runs. Always preceded by `coordinator.steering_decision`. |
| `proceed` | C: proceed or terminal | Continue to review or record a terminal/blocked result. |
| `advisory` | D: no-op | Surface the signal and take no corrective action. |

A released pod can make a child non-resumable, choosing fresh dispatch over in-place steering. Lack of another author does not itself force escalation: with accumulated feedback/context, a fresh same-author run can preserve prior work without changing lockout. Without context, escalate to human review. Autonomous budgets still bound retries.

## Events

| Event | When it fires | Payload |
| --- | --- | --- |
| `coordinator.steering_received` | A signal from any source is persisted and queued. | `directiveId`, `source`, `severity`, `verb`, `targetScope`, `feedback`, `treeHash` |
| `coordinator.steering_decision` | The coordinator records its A/B/C/D decision before executing the effect. | `directiveId`, `decision`, `rationale`, `subtaskIds`, `attempt` |
| `coordinator.steering` | Legacy directive lifecycle event for human steering. | `directiveId`, `kind`, `targetChildRunId`, `status`, `instruction` |

The web timeline renders `dispatch_fresh` as **fresh dispatch**, `in_place_steer` / `in-place` as **steered in place**, and `advisory` as **advisory noted** (`apps/web/src/components/LifecycleEventCard.tsx:260`).

## Failure and recovery semantics

| Case | Observable result | Recovery behavior |
| --- | --- | --- |
| Transient in-place revision commit failure | The child stays on the same run/worktree while commit is retried. | `AgentTurnExecutor` retries `CommitChanges` up to 3 attempts before surfacing failure. |
| Persistent child executor failure during in-place revision | Child run terminalizes with `run.failed` reason the bounded `{ message, errorCode, retryable }` public failure contract and the corresponding `workflow.step` is `failed`. | The coordinator preserves the steering instruction and emits a visible `dispatch_fresh` steering decision for failed targets. |
| Crash before revision launch or before first confirmed effect | The steering directive remains outstanding; it is not marked `applied`. | Recovery re-drives unconfirmed targets. Confirmed child effects are skipped so successful children are not re-injected. |
| Successful in-place revision | Same child run/worktree re-enters assembly after reaching `assemble_ready` or `completed`. | The directive is marked `applied` only when every target is assembly-eligible and every target child has a confirmed `SteeringRevisionExecution` marker. |

the bounded `{ message, errorCode, retryable }` public failure contract is terminal for coordinator child runs. It replaces the previous uninformative `watch_stream_completed_without_terminal_event` path for child executor throws, so operators see the executor that failed and the timeline gets a failed `workflow.step`.

## Budgets

| Bound | Default | Source |
| --- | --- | --- |
| Per-subtask recovery attempts | `3` | `CoordinatorSteeringService.MaxRecoveryAttempts` |
| Per-plan steering iterations | `6` | `CoordinatorSteeringDecider.DefaultMaxPlanSteeringIterations` |

When autonomous budgets exhaust, the decider chooses proceed and assembly escalates durably to `in_review`, stage review, with the run awaiting review. It does not latch terminal assembly-blocked. Human request-changes resets the autonomous budgets as a new supervised mandate; `HumanReviewRoundTrips` is telemetry, not a cap.

## Status and persistence

| State / record | Purpose |
| --- | --- |
| `SteeringDirective` | Stores the signal, status, chosen action, attempt, source/severity/scope, and tree hash. |
| `assembly_steering` work-plan status | Decision-in-progress lease for assembly-originated feedback. |
| `SteeringRevisionExecution` | Attempt-specific marker proving an in-place revision effect ran. |
| `RecoveryAttempts` on subtask | Per-subtask loop bound. |
| `SteeringIterations` on work plan | Per-plan loop bound. |

A human `redirect`/`amend`/`send` sent to `POST /api/runs/{id}/steer` while the coordinator is parked at the assembly human-review gate (`awaiting_review`) is delivered straight into the review gate rather than the child-turn queue (#226): `redirect`/`amend` become a request-changes decision on the same path as `POST /assembly/review` (settling `relayed`), and `send` becomes an advisory note (settling `applied`). When the gate is armed on a different API replica the directive is durably persisted with the terminal status `deferred` for the owning pod's poller to drain, and the endpoint answers `202 Accepted`. See [resilient assembly review](./resilient-assembly-review.md#operator-steering-at-the-review-gate-226).

## See also

- [Events reference](./events.md)
- [Coordinator reference](./coordinator.md)
- [Unified autonomous steering — Deep Dive](../deep-dive/unified-steering.md)

<!-- diagram-context:resilient-assembly-review-fig1:start -->
<details id="diagram-context-resilient-assembly-review-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Rejected work keeps useful context</td></tr>
<tr><td>takeaway</td><td>A steering decision chooses the effect; rejection does not always rotate the author.</td></tr>
<tr><td>group-title-0</td><td>FEEDBACK AND SCOPE</td></tr>
<tr><td>group-title-1</td><td>BOUNDED DIRECTION</td></tr>
<tr><td>group-title-2</td><td>AUTHOR CONTINUITY AND HUMAN ESCALATION</td></tr>
<tr><td>Gate request-changes</td><td>Gate request-changes</td></tr>
<tr><td>Gate request-changes</td><td>Structured target-file hints</td></tr>
<tr><td>Gate request-changes</td><td>not prose-inferred blame</td></tr>
<tr><td>Implicated + dependent</td><td>Implicated + dependent</td></tr>
<tr><td>Implicated + dependent</td><td>Rebuild closure without blame</td></tr>
<tr><td>Implicated + dependent</td><td>structured TARGET_FILES</td></tr>
<tr><td>Signal + decision</td><td>Signal + decision</td></tr>
<tr><td>Signal + decision</td><td>Persist explicit direction</td></tr>
<tr><td>Signal + decision</td><td>accumulated context</td></tr>
<tr><td>In-place revision</td><td>In-place revision</td></tr>
<tr><td>In-place revision</td><td>Same author and session</td></tr>
<tr><td>In-place revision</td><td>no reset-to-pending</td></tr>
<tr><td>Fresh dispatch</td><td>Fresh dispatch</td></tr>
<tr><td>Fresh dispatch</td><td>Scoped author selection</td></tr>
<tr><td>Fresh dispatch</td><td>handoff with context</td></tr>
<tr><td>No alternate author</td><td>No alternate author</td></tr>
<tr><td>No alternate author</td><td>Context permits same author</td></tr>
<tr><td>No alternate author</td><td>bounded conscious fallback</td></tr>
<tr><td>Human escalation</td><td>Human escalation</td></tr>
<tr><td>Human escalation</td><td>No context or budget left</td></tr>
<tr><td>Human escalation</td><td>durable review request</td></tr>
<tr><td>Human decision</td><td>Human decision</td></tr>
<tr><td>Human decision</td><td>Approve, change or decline</td></tr>
<tr><td>Human decision</td><td>no wall-clock timeout</td></tr>
<tr><td>Fresh autonomous budget</td><td>Fresh autonomous budget</td></tr>
<tr><td>Fresh autonomous budget</td><td>Only human changes reset it</td></tr>
<tr><td>Fresh autonomous budget</td><td>no human-round-trip cap</td></tr>
<tr><td>e0</td><td>scope</td></tr>
<tr><td>e1</td><td>signal</td></tr>
<tr><td>e2</td><td>resume</td></tr>
<tr><td>e3</td><td>fresh</td></tr>
<tr><td>e4</td><td>no alt</td></tr>
<tr><td>e5</td><td>context</td></tr>
<tr><td>e6</td><td>no context</td></tr>
<tr><td>e7</td><td>Proceed</td></tr>
<tr><td>e8</td><td>await</td></tr>
<tr><td>e9</td><td>changes</td></tr>
<tr><td>e10</td><td>retry</td></tr>
<tr><td>groups</td><td>FEEDBACK AND SCOPE; BOUNDED DIRECTION; AUTHOR CONTINUITY AND HUMAN ESCALATION</td></tr>
</tbody></table>
</details>
<!-- diagram-context:resilient-assembly-review-fig1:end -->

<!-- diagram-context:unified-steering-fig1:start -->
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
<!-- diagram-context:unified-steering-fig1:end -->
