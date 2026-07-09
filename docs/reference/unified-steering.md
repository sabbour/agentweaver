# Unified autonomous steering — Reference

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

## Events

| Event | When it fires | Payload |
| --- | --- | --- |
| `coordinator.steering_received` | A signal from any source is persisted and queued. | `directiveId`, `source`, `severity`, `verb`, `targetScope`, `feedback`, `treeHash` |
| `coordinator.steering_decision` | The coordinator records its A/B/C/D decision before executing the effect. | `directiveId`, `decision`, `rationale`, `subtaskIds`, `attempt` |
| `coordinator.steering` | Legacy directive lifecycle event for human steering. | `directiveId`, `kind`, `targetChildRunId`, `status`, `instruction` |

The web timeline renders `dispatch_fresh` as **fresh dispatch**, `in_place_steer` / `in-place` as **steered in place**, and `advisory` as **advisory noted** (`apps/web/src/components/LifecycleEventCard.tsx:260`).

## Budgets

| Bound | Default | Source |
| --- | --- | --- |
| Per-subtask recovery attempts | `3` | `CoordinatorSteeringService.MaxRecoveryAttempts` |
| Per-plan steering iterations | `6` | `CoordinatorSteeringDecider.DefaultMaxPlanSteeringIterations` |

When a budget is exhausted, the decider chooses `proceed` and records the rationale. Assembly may surface this as `assembly_blocked` with reason `steering_budget_exhausted`.

## Status and persistence

| State / record | Purpose |
| --- | --- |
| `SteeringDirective` | Stores the signal, status, chosen action, attempt, source/severity/scope, and tree hash. |
| `assembly_steering` work-plan status | Decision-in-progress lease for assembly-originated feedback. |
| `SteeringRevisionExecution` | Attempt-specific marker proving an in-place revision effect ran. |
| `RecoveryAttempts` on subtask | Per-subtask loop bound. |
| `SteeringIterations` on work plan | Per-plan loop bound. |

## See also

- [Events reference](./events.md)
- [Coordinator reference](./coordinator.md)
- [Unified autonomous steering — Deep Dive](../deep-dive/unified-steering.md)
