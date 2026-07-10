# Resilient assembly-review loop — Reference

Terse reference for the **resilient assembly-review loop** introduced in v0.9.17-rc1: configuration knobs,
coordinator status transitions, emitted events, and the lockout protocol contract. For the conceptual
explanation see the [deep dive](../deep-dive/resilient-assembly-review.md); for the operator experience see
the [user guide](../experience/resilient-assembly-review.md).

## Configuration

| Config key | Default | Meaning |
|---|---|---|
| `Coordinator:StaleLockThresholdSeconds` | `15` | Age in seconds above which a `.git/index.lock` file is considered stale and eligible for removal between commit retry attempts. The check also verifies no live `git` process owns the lock; on any uncertainty the file is left untouched and the fault surfaces as a visible `child-turn-failed` terminal. Applies per child/revision run's own worktree only. Source: `WorktreeManager.ClearStaleIndexLock`. |

**Max human review round-trips** (default `3`) is defined as the constant
`CoordinatorSteeringDecider.DefaultMaxHumanReviewRoundTrips`. There is no separate config-file key at this
time — it mirrors the existing `DefaultMaxPlanSteeringIterations` constant pattern. To track the live count
inspect the `WorkPlan.HumanReviewRoundTrips` database column or the `coordinator.steering` event
(`budgetReset`, `humanRoundTrip` fields).

## Status transitions

The following `WorkPlanStatus` transitions are new or changed in this release:

| Previous behavior | New behavior |
|---|---|
| Budget exhausted → `AssemblyBlocked` (terminal, `reason=steering_budget_exhausted`) — run parks indefinitely. | Budget exhausted → `InReview` (stage `"review"`) — run escalates to the human-review gate. The assembled work is immediately reviewable. |
| `AssemblyBlocked` with `steering_budget_exhausted` was not eligibility-recoverable. | `InReview` is the existing human-review state; all existing recovery, gate-arm, and crash-resume paths apply unchanged. |

### Full transition table (assembly phase)

| From | Event | To | Notes |
|---|---|---|---|
| `Assembling` / `AssemblySteering` | Autonomous budget exhausted | `InReview`, stage `"review"` | Guarded CAS via `TryEscalateToInReviewAsync`. Second replica no-ops. |
| `InReview` | Human approves | `Assembling` → merge → `assembly_complete` | Existing approve path, unchanged. |
| `InReview` | Human declines | `assembly_declined` (terminal) | Existing decline path, unchanged. |
| `InReview` | Human request-changes (round-trip ≤ cap) | `AssemblySteering` | Budget reset; autonomous loop restarts. |
| `InReview` | Human request-changes (round-trip > cap) | `InReview` (stays, re-parked) | Autonomy paused; human gate stays open. Never terminal. |

## Emitted events

All existing event types; no new event type is introduced. Fields added to existing events:

| Event | New / changed fields | When emitted |
|---|---|---|
| `coordinator.steering_decision` | `decision="proceed"`, `escalation="human_review"` | When the decider returns `Proceed` (budget exhausted) and the coordinator escalates instead of terminating. |
| `coordinator.assembly_review_requested` | `gateKind="human-review"`, `reason="steering_budget_exhausted"`, `treeHash`, `integrationBranch`, `includedSubtaskIds` | Immediately after the durable review-request is written. Same event as the normal happy-path human-review gate. |
| `coordinator.steering` | `budgetReset=true\|false`, `humanRoundTrip=<n>` | Emitted when a human request-changes is received after escalation; records whether the reset was applied and the current round-trip count. |
| `run.failed` (child run) | `reason=commit_failed_persistent`, `evidence=<exception summary + lock diagnostics>` | Emitted when a persistent commit fault exhausts retries in the child pipeline; the child run fails visibly with structured evidence instead of a silent stream drain. |

### `coordinator.assembly_review_requested` — accumulated feedback payload

When `reason="steering_budget_exhausted"`, the review-request context includes the **accumulated gate
feedback** from all prior autonomous rounds. This is built by `BuildAccumulatedGateFeedbackAsync`
(bounded to 32 directives, 2000 characters each) and structured by gate source and round number, so the
human can see exactly why autonomy could not converge. The same payload is visible in the review card in
the web UI.

## Reviewer-rejection lockout protocol

When any gate source issues a `request-changes` (rejection) against a subtask:

| Step | What happens |
|---|---|
| Lock out the author | The current subtask author is atomically appended to `Subtask.LockedOutAgents` (a persisted JSON set). |
| Select a different agent | `roster \ LockedOutAgents` via `CoordinatorOrchestratorExecutor.ResolveRoster` + `SelectRosterMember`. |
| Dispatch with context | `RunOrchestrator.StartChildRevisionHandoffAsync` reuses the prior child's worktree + branch; carries the full accumulated feedback bundle; mints a **new** SDK session (never resumes the locked-out author's session). |
| Deadlock or budget exhaustion | All eligible agents locked out, or budget exhausted on the lockout path → Fix-B human-review escalation. Never a terminal. |

Advisory / steer feedback (not a rejection) keeps the same agent in place (`StartRevisionAsync`); no
lockout is applied.

## Subtask `LockedOutAgents` field

`Subtask.LockedOutAgents` is a JSON-serialized string set that records which agent names are locked out
from producing the next revision of that specific subtask artifact. It was a dormant reserved column since
the initial `20260617224038_AddCoordinatorWorkPlan` migration; v0.9.17-rc1 is the first release to read
and write it. No new migration is required.

## Stale git-lock diagnostics

`WorktreeManager.ClearStaleIndexLock` returns an `IndexLockClearResult` record that is included in the
`ChildTurnFailedOutput.Evidence` string and the `run.failed` event:

| Field | Meaning |
|---|---|
| `lock_present` | Whether `<gitdir>/index.lock` existed at the time of the check. |
| `cleared` | Whether the lock was deleted. |
| `age_s` | Age of the lock file in seconds (if present). |
| `live_git_proc` | Whether a live `git` process was detected (prevents deletion when true). |
| `detail` | Free-text reason the clear was skipped, if applicable. |

The `gitdir` is resolved from the worktree's `.git` file for linked worktrees (handles the pointer-file
indirection) before checking the lock path, so the diagnostics always refer to the actual index location.

## See also

- [Resilient assembly-review — Deep Dive](../deep-dive/resilient-assembly-review.md) — design rationale, state machine, and source map.
- [Resilient assembly-review — User Guide](../experience/resilient-assembly-review.md) — what operators and users observe.
- [Coordinator reference](./coordinator.md) — the full coordinator status model and event index.
- [Unified steering reference](./unified-steering.md) — `SteeringSignal`, `SteeringDirection`, and the decider contract.
