# Resilient assembly-review loop — Reference

Terse reference for the **resilient assembly-review loop** introduced in v0.9.17-rc1: configuration knobs,
coordinator status transitions, emitted events, and the lockout protocol contract. For the conceptual
explanation see the [deep dive](../deep-dive/resilient-assembly-review.md); for the operator experience see
the [user guide](../experience/resilient-assembly-review.md).

## Configuration

| Config key | Default | Meaning |
|---|---|---|
| `Coordinator:StaleLockThresholdSeconds` | `15` | Age in seconds above which a `.git/index.lock` file is considered stale and eligible for removal between commit retry attempts. The check also verifies no live `git` process owns the lock; on any uncertainty the file is left untouched and the fault surfaces as a visible `child-turn-failed` terminal. Applies per child/revision run's own worktree only. Source: `WorktreeManager.ClearStaleIndexLock`. |

**Human review round-trips are no longer capped.** The former
`CoordinatorSteeringDecider.DefaultMaxHumanReviewRoundTrips` constant has been **removed**. A human
request-changes at the assembly review gate now **unconditionally** resets the autonomous steering budget —
a supervised, deliberate human request must always be honored, so capping it (and silently re-parking the
run) was a dead-end. Each granted round is still bounded by the autonomous `DefaultMaxPlanSteeringIterations`
per steering pass. The `WorkPlan.HumanReviewRoundTrips` column is retained purely as telemetry (surfaced on
the `coordinator.steering` event's `humanReviewRoundTrip` field); it no longer gates anything.

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
| `InReview` | Human request-changes (any round-trip) | `AssemblySteering` | Budget **unconditionally** reset (no cap); autonomous loop restarts under the human's feedback. `HumanReviewRoundTrips` is incremented as telemetry only. |

## Emitted events

All existing event types; no new event type is introduced. Fields added to existing events:

| Event | New / changed fields | When emitted |
|---|---|---|
| `coordinator.steering_decision` | `decision="proceed"`, `escalation="human_review"` | When the decider returns `Proceed` (budget exhausted) and the coordinator escalates instead of terminating. |
| `coordinator.assembly_review_requested` | `gateKind="human-review"`, `reason="steering_budget_exhausted"`, `treeHash`, `integrationBranch`, `includedSubtaskIds` | Immediately after the durable review-request is written. Same event as the normal happy-path human-review gate. |
| `coordinator.steering` | `source`, `humanReviewRoundTrip=<n>`, `note` | Emitted when a human request-changes is received after escalation. The autonomous budget is **always** reset (there is no cap); `humanReviewRoundTrip` is the telemetry-only round count. |
| `coordinator.assembly_implicated_scope_fallback` | `workPlanId`, `source`, `reviewer`, `reason` (`no_target_files_field` \| `target_files_matched_nothing`), `namedFiles`, `touchedFiles`, `contributorIds` | Emitted when the #223 implicated-subtask scoping reverts to the broad all-contributors set because the reviewer's structured `TARGET_FILES:` hint was missing or reverse-mapped to nothing. Makes the fail-safe reversion observable. |
| `coordinator.assembly_changes_requested` | `workPlanId`, `redispatchSubtaskIds`, `redispatchedSubtaskIds`, `implicatedSubtaskIds`, `dependentSubtaskIds`, `feedback` | When a gate requests changes. `implicatedSubtaskIds` are the reviewer-named (lockout-eligible) subtasks; `dependentSubtaskIds` are their transitive dependents (rebuilt, never locked out); `redispatchSubtaskIds` is their union. |
| `run.failed` (child run) | `reason=commit_failed_persistent`, `evidence=<exception summary + lock diagnostics>` | Emitted when a persistent commit fault exhausts retries in the child pipeline; the child run fails visibly with structured evidence instead of a silent stream drain. |

### `coordinator.assembly_review_requested` — accumulated feedback payload

When `reason="steering_budget_exhausted"`, the review-request context includes the **accumulated gate
feedback** from all prior autonomous rounds. This is built by `BuildAccumulatedGateFeedbackAsync`
(bounded to 32 directives, 2000 characters each) and structured by gate source and round number, so the
human can see exactly why autonomy could not converge. The same payload is visible in the review card in
the web UI.

## Implicated-subtask scoping (#223)

A gate request-changes no longer resets/locks out **every** subtask that touched a file. The reviewer
(Rubberduck or Build & Test) may emit an optional structured `TARGET_FILES:` directive line naming the
repo-relative paths it implicated; `ReviewTargetFiles.Parse` turns it into `AssemblyReviewDecision.TargetFiles`.
The coordinator (`AssemblyPlanning.ScopeImplicatedSubtasks`) reverse-maps those paths onto the
assembly-eligible subtasks that actually committed them, then `AssemblyPlanning.TransitiveDependents` sweeps
their dependents:

| Set | Members | Author lockout? |
|---|---|---|
| **Implicated** | subtasks that committed a named file (`ScopeImplicatedSubtasks`) | Yes |
| **Re-dispatch** | implicated ∪ transitive dependents | dependents: **No** |

If the `TARGET_FILES:` hint is absent (`no_target_files_field`) or matches no subtask
(`target_files_matched_nothing`), scoping falls back to the whole contributor set (fail-safe) and emits
`coordinator.assembly_implicated_scope_fallback`. The same helpers back both the live steering path
(`RouteAssemblyGateThroughSteeringAsync`) and the conscious fresh-dispatch executor (`RequestChangesAsync`).

## Operator steering at the review gate (#226)

While the run is parked at the human-review gate (`run.status == awaiting_review`,
`coordinator_steerable == true`), `POST /api/runs/{id}/steer` is honored (previously `redirect`/`amend`
returned `queued` but were silently dropped). The verbs map as follows:

| `/steer` verb at `awaiting_review` | Effect | Directive status |
|---|---|---|
| `redirect` / `amend` | Delivered as a request-changes review decision — the **same** path as `POST /assembly/review {request_changes}`. The parked loop routes it via `RouteAssemblyGateThroughSteeringAsync` with #223 scoping + the unconditional budget reset. | `relayed`, or `deferred` when the gate is armed on another replica |
| `send` | Advisory note on the coordinator timeline; the gate stays armed — no decision, no budget reset. | `applied` |

**Scoping.** A bare `redirect`/`amend` has no target files, so it uses the broad all-contributors fallback
(identical to `/assembly/review` with no `target_files`; emits `coordinator.assembly_implicated_scope_fallback`).
Set `targetChildRunId` on the steer request to narrow: the targeted subtask's committed files flow through
the same `ScopeImplicatedSubtasks` reverse-map, scoping to that subtask ∪ its co-touching subtasks.

**HTTP status.** `POST /steer` returns `201 Created` normally, and `202 Accepted` when the decision is
deferred to another replica's poller (directive `deferred`) — mirroring the `/assembly/review` deferred
response. It is **never** left silently `queued` (the pre-#226 drop bug). The new `deferred` value joins the
`pending → queued → relayed → applied` steering-directive lifecycle.

## Reviewer-rejection lockout protocol

When any gate source issues a `request-changes` (rejection), the lockout applies to the **implicated**
subtasks only (#223 — never every file-toucher):

| Step | What happens |
|---|---|
| Lock out the author | The current subtask author is atomically appended to `Subtask.LockedOutAgents` (a persisted JSON set). |
| Select a different agent | `roster \ LockedOutAgents` via `CoordinatorOrchestratorExecutor.ResolveRoster` + `SelectRosterMember`. |
| Dispatch with context | `RunOrchestrator.StartChildRevisionHandoffAsync` reuses the prior child's worktree + branch; carries the full accumulated feedback bundle; mints a **new** SDK session (never resumes the locked-out author's session). |
| Deadlock or budget exhaustion | All eligible agents locked out, or budget exhausted on the lockout path → Fix-B human-review escalation. Never a terminal. |

Transitive dependents of the implicated subtasks are re-dispatched to rebuild against the revised contract,
but their authors are **never** locked out (#223 — locking a blameless dependent re-creates the
roster-exhaustion deadlock).

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
