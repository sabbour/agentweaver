# Resilient assembly-review loop — Deep Dive

Shipped in **v0.9.17-rc1**, the resilient assembly-review loop eliminates the `assembly_blocked` dead-end that
previously stranded coordinator runs when the autonomous steering budget was exhausted. It also makes every
phase of the review cycle more robust: revisions carry accumulated context, resumable rejected work can
stay with its author while fresh-dispatch decisions attempt a context-preserving handoff, and child-turn commit faults surface as typed, recoverable
events instead of silent stream-drain failures.

For config keys and status codes see the [reference](../reference/resilient-assembly-review.md). For the
operator-facing view of what changed see the [user guide](../experience/resilient-assembly-review.md).

## The problem: why runs used to dead-end

The assembly gate runs an autonomous steering loop to converge work toward approval. Before v0.9.17-rc1,
when that loop exhausted its budget the coordinator wrote a **terminal `WorkPlanStatus.AssemblyBlocked`**
and emitted `CoordinatorAssemblyBlocked`. Because `assembly_blocked` with reason
`steering_budget_exhausted` is not eligibility-recoverable, the reconciler could not restart the run — it
parked indefinitely waiting for an external signal that the platform never sent. The assembled work was
ready; a human could not reach it
(`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1735–1830` before this change).

Two coupled sub-problems drove that outcome:

1. **Budget-exhaustion wrote terminal instead of escalating.** The steering decider's own rationale
   described the intent as *"escalate to human review"*, but the code path wrote `AssemblyBlocked`.
2. **In-place revision fell back to context-dropping re-dispatch 2/3 of the time.** A stale
   `.git/index.lock` left by a prior crashed process caused the post-turn commit to fail; the three retry
   attempts all hit the same lock because nothing cleared it between attempts → rethrow → the child run
   failed → a fresh-pod `dispatch_fresh` started with only the latest round's feedback and a blank worktree
   (all prior context lost).

Each lost context round burned steering budget faster, hitting the terminal state sooner. The two fixes are
complementary: Fix-A raises the in-place convergence rate; Fix-B guarantees the tail still reaches a human.

## End-to-end resilient assembly flow

### Fix-B: budget-exhausted escalation (the headline change)

When `CoordinatorSteeringDecider.DecideAsync` returns `Proceed` (budget exhausted), the coordinator now
calls `EscalateToHumanReviewAsync`, which mirrors the existing `human-review` gate block verbatim:

1. **Guarded CAS transition** — `CoordinatorAssemblyStore.TryEscalateToInReviewAsync` does an
   `ExecuteUpdateAsync` `WHERE Status ∈ {AssemblySteering, Assembling} → InReview, stage "review"`. A
   concurrent replica that already set `InReview` no-ops (no double-escalation).
2. **Durable review-request** — `UpsertReviewRequestAsync` persists the owner, integration branch, and tree
   hash before settling the directive. The review-requested **event** carries the escalation reason and
   accumulated gate feedback (bounded to 32 directives × 2000 characters by
   `BuildAccumulatedGateFeedbackAsync`); those are not fields on the review row. Recovery uses
   `InReview → ResumeInReviewAsync` without rebuilding an already-recorded review artifact
   (`CoordinatorAssemblyService.cs:1321–1357`, `:2882–2986`).
3. **Settle the directive** — `MarkDirectiveAppliedAsync` is called only after the review request is
   durably open. The human then owns the loop; the directive is never blocked on how long the human takes.
4. **Live-await** — `AwaitReviewDecisionAsync` → `ApplyReviewDecisionAsync`. Approve → merge → complete.
   Decline → `assembly_declined` terminal. Request-changes → **unconditional** budget reset + a new steering
   loop (below).

The emit sequence:
- `coordinator.steering_decision` records the chosen direction and rationale.
- `coordinator.assembly_review_requested { gateKind="human-review", escalated=true, reason, treeHash, integrationBranch, accumulatedFeedback }` carries the escalation rationale; the normal authored-gate event has a different payload.

This is the same event the human-review gate emits on the normal happy path, so the UI opens the same
review card with the exhaustion reason visible.

**Why not make `AssemblyBlocked` recoverable?** `AssemblyBlocked` semantics = "the plan can't proceed,
wait for external input." `InReview` is the state whose full machinery (gate arm, deferred-decision poll,
crash recovery) is built for "a human must decide now." Reusing it is the root-cause fix; patching
`AssemblyBlocked` recovery would be symptom-plastering.

### Human request-changes always resets the steering budget (no round-trip cap)

When the human submits request-changes after an escalation, `RouteAssemblyGateThroughSteeringAsync`
(source `human-review`) **unconditionally** resets the autonomous steering budget via
`CoordinatorSteeringDecider.ResetSteeringBudgetAsync` — there is **no** round-trip cap. The reset atomically
zeros the plan's `SteeringIterations` and the re-dispatched subtasks' `RecoveryAttempts` (guarded CAS,
single transaction) so autonomy can converge again under the human's specific feedback. Without the reset,
the human's very first steer would immediately re-hit `Proceed` (a livelock).

**Why the cap was dropped (`DefaultMaxHumanReviewRoundTrips` removed).** A human request-changes is a
supervised, deliberate action. The old cap (default 3) silently stopped honoring explicit human requests
once the count was exceeded: the run re-parked at review and ignored the human's guidance — a dead-end. Each
granted round is still bounded by the **autonomous** budget (`DefaultMaxPlanSteeringIterations`), which is
exactly what bounds the *unsupervised* loop; only the *human*-round-trip ceiling was removed.

`WorkPlan.HumanReviewRoundTrips` is still incremented (`IncrementHumanReviewRoundTripAsync`) and surfaced on
the `coordinator.steering` event (`humanReviewRoundTrip`), but purely as a telemetry/observability signal —
it no longer gates anything.

**Loop-prevention invariant:** autonomous gates (rubberduck/rai/build-test/agent) can never call
`ResetSteeringBudgetAsync` on themselves. Only `source == human-review` directives trigger the reset.

### Operator steering at the review gate (#226)

While a run is parked at the assembly human-review gate (`run.status == awaiting_review`,
`coordinator_steerable == true`), an operator can talk to the coordinator with
`POST /api/runs/{id}/steer`. Before #226, a `redirect`/`amend` sent there returned `201` with
`status: queued` but **nothing drained it** — the parked assembly loop polls the review gate, not the
steering queue, so the directive was silently dropped. That contradicted the documented `coordinator_steerable`
promise that operators can steer while the gate is open (`Dtos.cs:174-176`).

#226 intercepts these verbs in `CoordinatorSteeringService.SteerAsync` **before** the normal resume/queue
fork (`TryDeliverAtAssemblyReviewGateAsync`), and delivers the human's intent through the *same* mechanism
`POST /assembly/review {request_changes}` uses:

- **redirect / amend** → translated into an `AssemblyReviewDecision { RequestChanges = true, Feedback =
  instruction }` and delivered via `CoordinatorAssemblyReviewPersistence.DeliverDecisionAsync`. The parked
  loop wakes and owns `RouteAssemblyGateThroughSteeringAsync` as the single writer, reusing the #223
  file-scoped implication + transitive dependents and the cap-drop **unconditional** steering-budget reset.
  `amend` maps to the same `request_changes`; the decider still prefers in-place when resumable (it is not
  force-pinned to in-place). The directive settles `relayed` (delivered into the live gate on this pod) —
  **never** `queued`.
- **Scoping** — a bare redirect/amend carries no target files, so it defaults to the broad
  all-contributors fallback (identical to `/assembly/review` with no `target_files` → `ScopeFallbackNoField`,
  emitting `coordinator.assembly_implicated_scope_fallback`). Operators can **optionally narrow** by setting
  `targetChildRunId`: `ResolveTargetFilesForChildAsync` resolves that subtask's committed files and feeds
  them through the same `AssemblyPlanning.ScopeImplicatedSubtasks` reverse-map, scoping to that subtask ∪
  any co-touching subtasks. Prose is never parsed for files or subtask ids.
- **send** at the gate carries no change request, so it is delivered as an **advisory** note on the
  coordinator timeline (`DeliverAdvisorySendAtReviewGateAsync`): the gate stays armed, no budget reset, no
  decision; the directive settles `applied` — never left `queued` forever.
- **Cross-replica** — if the review gate is armed on a *different* API replica, the decision is durably
  persisted (`SteeringStatus.Deferred`) for the owning pod's poller to drain; the directive settles
  `deferred` and the `/steer` endpoint returns **HTTP 202** (mirroring `/assembly/review`'s deferred `202`)
  instead of `201`.

**B3 single-writer invariant:** the HTTP thread only *delivers* the decision; it never runs
`RouteAssemblyGateThroughSteeringAsync` itself — the parked assembly loop remains the single writer of the
subtask/work-plan rows.

### Fix-A: reliable child-turn terminal emission

The child run graph previously had no failure→terminal edge. A post-turn commit fault rethrew → the watcher
recorded a `failed` step but did not terminalize → stream drained → `watch_stream_completed_without_terminal_event`
fallback → the child fell into conscious `dispatch_fresh` (context dropped). Fix-A has two layers:

**Layer 1 — stale lock recovery (FIX 1, the rate driver).** `AgentTurnExecutor.CommitChangesWithRetryAsync`
now calls `IWorktreeOperations.TryClearStaleIndexLock(worktreePath)` between retry attempts.
`WorktreeManager.ClearStaleIndexLock` resolves the actual `gitdir` (handles linked-worktree `.git`
pointer files), checks the `index.lock` mtime against `Coordinator:StaleLockThresholdSeconds` (default
15 s), verifies no live `git` process owns it, and only then deletes the file. The clear is best-effort:
on uncertainty it does NOT delete → the persistent fault surfaces as a typed failure terminal (Layer 2).
This converts lock-contention commit faults from context-dropping `dispatch_fresh` failures to clean
`assemble_ready` outcomes on the same worktree.

**Layer 2 — graph-native failure terminal (FIX 2, the invariant).** `AgentTurnOutput` now carries
nullable `TerminalFailureReason` and `TerminalFailureEvidence`. When all retry attempts are exhausted
(persistent fault), `AgentTurnExecutor` **returns** a typed failure output instead of rethrowing. The
child graph (`RunWorkflowFactory.cs`) routes via two conditional edges:

- `agent → child-assemble-ready` when `TerminalFailureReason is null` (success, existing path).
- `agent → child-turn-failed` (new terminal node) when `TerminalFailureReason is not null`.

`RunWatchLoopService.HandleTerminalOutputAsync` maps `ChildTurnFailedOutput` → child run Failed (visible,
structured) → subtask Failed → coordinator's `failedTargets` → conscious `dispatch_fresh` with accumulated
context. An unhandled in-turn throw still hits the watcher `ExecutorFailedEvent` backstop (defense-in-depth).

**Invariant:** for every child/revision run, after `agent.turn.end` the workflow yields exactly one terminal
`WorkflowOutputEvent`. `watch_stream_completed_without_terminal_event` is no longer reachable via the
handled commit-fault path.

### Context-preserving revisions and `AccumulatedReviewFeedback`

Before this change, the `dispatch_fresh` fallback overwrote `Subtask.RecoveryGuidance` with only the
latest round's feedback — prior feedback was silently discarded (amnesiac re-dispatch). Now both in-place
and fresh-dispatch revision paths use `BuildAccumulatedGateFeedbackAsync`, which reads all persisted
`SteeringDirective` rows (one per source/round, crash-safe by construction) and assembles a structured
feedback bundle keyed by gate source and round number. This is the `AccumulatedReviewFeedback` contract
shared between `CoordinatorAssemblyService` and `RunOrchestrator`.

The fresh-dispatch path (`ConsciousDispatchFreshFallbackAsync`) also carries a pointer to the prior child's
integration-branch diff, so the new agent builds on prior committed work instead of starting blank.

### Scoped re-dispatch: implicated subtasks vs. transitive dependents (#223)

Before #223, an assembly-gate request-changes (from any of rubberduck / rai / build-test / human) reset —
and locked out — **every** subtask that had touched a file: the coordinator used the raw
`touchedFilesBySubtask.Keys`. On a non-trivial plan this locked out every author at once, exhausting the
roster and deadlocking the whole run (an all-agent lockout). #223 replaces that blast radius with a
structured, reviewer-scoped selection.

**The reviewer emits a structured hint.** A Rubberduck or Build & Test reviewer returning a REVISE verdict
may also write a dedicated `TARGET_FILES:` directive line listing the repo-relative paths it actually
implicated. `ReviewTargetFiles.Parse` (called in `RubberduckTurnExecutor` / `BuildTestTurnExecutor`) parses
that line into `AssemblyReviewDecision.TargetFiles`. This is machine-readable **structured output** — a line
the reviewer is explicitly instructed to write — **never** natural-language prose scraping. The directive
line is stripped from the human-facing feedback (`ReviewTargetFiles.IsDirectiveLine`).

**The coordinator scopes to the implicated subtasks.** `AssemblyPlanning.ScopeImplicatedSubtasks`
reverse-maps the `TARGET_FILES` hint onto only the assembly-eligible subtasks that actually committed one of
those files — a prose/PRD/UX subtask that committed only unnamed files is **excluded**. Then
`AssemblyPlanning.TransitiveDependents` sweeps every subtask that depends, directly or transitively, on an
implicated subtask (the `(SubtaskId, DependsOnSubtaskId)` edges walked in reverse).

**Two distinct sets, used differently:**

| Set | Members | Author lockout? | Why |
|---|---|---|---|
| **Implicated** (potential lockout set) | reviewer-named subtasks, or the observable contributor fallback (`ScopeImplicatedSubtasks`) | **Only if fresh-dispatch rotation is selected and feasible** | resumable revisions retain their author; unrelated authors must not be locked out |
| **Re-dispatch** set | implicated ∪ transitive dependents (`implicated.Concat(TransitiveDependents)`) | dependents: **No** | a dependent did nothing wrong but must rebuild against the revised contract |

Locking out a blameless dependent's author would re-create the very roster-exhaustion deadlock #223 exists
to prevent, so dependents are re-dispatched (`RedispatchDependentsAsync`) **without** lockout — and only if
they already reached `assemble_ready`/`completed` (a still-running dependent needs no reset, keeping crash
re-drives idempotent).

**Fail-safe fallback (observable).** The structured hint is optional. When it is missing, or present but
matching nothing, `ScopeImplicatedSubtasks` returns the **whole contributor set** (a request-changes must
always reset *something*) and sets an out-param so the reversion is visible rather than silent. The
coordinator then emits `coordinator.assembly_implicated_scope_fallback` (`EmitImplicatedScopeFallback`) with
the reason:

- `no_target_files_field` — the reviewer emitted no `TARGET_FILES:` directive at all.
- `target_files_matched_nothing` — a directive was present but reverse-mapped to no subtask.

Both branches fall back to the prior broad touched-files behavior — fail-safe, just observable.

Both the live steering path (`RouteAssemblyGateThroughSteeringAsync`) and the conscious fresh-dispatch
executor (`RequestChangesAsync`) use the **same** `ScopeImplicatedSubtasks` + `TransitiveDependents` helpers,
and the persisted steering directive carries the implicated set so a crash-recovery re-drive recomputes an
identical re-dispatch closure from the same plan edges.

### Resumability-aware reviewer rejection

A **rejection** (gate feedback with severity `request-changes`) first goes through the decider.
`InPlaceSteer` resumes the same author's session without lockout; `DispatchFresh` attempts the rotation
protocol below, scoped to the **implicated** subtasks only (structured targets, or the documented broad
fallback). There is no post-decision override forcing every rejection to rotate
(`CoordinatorAssemblyService.cs:2323–2370`; `CoordinatorAssemblyServiceTests.cs:1090–1130`):

1. For each implicated subtask, the current author is atomically appended to `Subtask.LockedOutAgents` (a
   dormant column that existed in the schema since the initial migration but was never read/written; no new
   migration needed).
2. The coordinator selects a DIFFERENT eligible agent — `roster \ LockedOutAgents` via
   `CoordinatorOrchestratorExecutor.ResolveRoster` + `SelectRosterMember`.
3. The revision is dispatched via `RunOrchestrator.StartChildRevisionHandoffAsync`: the new agent
   **reuses the prior child's worktree and branch** (the work is preserved; only authorship rotates) and
   receives the full accumulated feedback bundle injected via the task path. A **new SDK session** is
   minted — the rotated agent never inherits the locked-out author's session identity.
4. The re-dispatched child honors the FIX 2 terminal-emission invariant identically (same conditional
   edge routing; same one-terminal guarantee).

The implicated subtasks' already-satisfied **transitive dependents** are additionally re-dispatched as needed
to rebuild against the revised contract, but their authors are **never** locked out (#223).

#### Single-eligible-agent deadlock: degrade vs. escalate (#233)

`ExecuteLockoutRotationAsync` cannot always rotate. A domain whose blueprint has exactly **one** eligible
agent has no *other* author to hand the revision to once the rejected author is appended to
`LockedOutAgents`: `RotationSelector.SelectRotationAuthor` returns `null` and the sole author is recorded in
the deadlock roster. Before #233 this dead-ended autopilot — the **first** rejection in a single-eligible
domain escalated straight to human review (`lockout_deadlock`) even though the revision was otherwise
recoverable, exactly the systemic wedge #233 reports (live staging incident, run `825ea158`).

#233 splits the deadlock branch by whether there is accumulated context to carry — `hasContext` = a
non-empty latest `feedback` **or** ≥1 prior review round (`BuildPriorReviewRoundsAsync`):

| Deadlock cause | Rationale | What happens |
|---|---|---|
| No alternate eligible agent **and** context to carry (single-eligible domain — the norm for a one-agent blueprint domain) | `single_eligible_agent` | **Degrade** the strict cross-agent lockout to a **same-author fresh re-dispatch** with full context via `ConsciousDispatchFreshFallbackAsync`. The sole author revises in a fresh pod carrying the accumulated feedback + prior worktree branch; `AssignedAgent` is **kept**, `LockedOutAgents` is **not** mutated (locking the only author would just re-create the deadlock); dependents are re-dispatched without lockout. Emits `coordinator.steering_decision { decision="dispatch_fresh", rationale="single_eligible_agent…" }`. |
| No context to carry (`!hasContext` — the initial deadlock value) | `lockout_no_context` | **Escalate** to the Fix-B human-review gate. Degrading here would carry nothing and reproduce the amnesia loop the #220/#223 context-carry prevents. Emits `coordinator.steering_decision { decision="proceed", disposition="rejection", rationale="lockout_no_context…" }`. |

The degrade is **not** an unbounded loop. `ResetSubtasksToPendingAsync` does **not** reset
`Subtask.RecoveryAttempts`, so the same-author revision keeps consuming the per-subtask recovery budget
(`CoordinatorSteeringService.MaxRecoveryAttempts` = 3). Once that budget is exhausted,
`CoordinatorSteeringDecider.DecideAsync` (via `SteeringPolicy.Decide`, over-budget → `Proceed`) flips the
directive and this gate escalates to human review — never a terminal wedge, never a blind rotation to an
unrelated agent. A **mixed** directive (some targets rotatable, some single-eligible deadlocked) degrades
**all** its targets uniformly to a same-author fresh re-dispatch (the rotatable ones simply keep their
current author), so no target is silently dropped.

This makes the pod-per-run single-eligible path consistent with the already-accepted warm-pool path: when
`ReleasePodOnSuspend=false` the target's pod stays alive and resumable, so the decider already picks
`InPlaceSteer` (same author, context preserved, no lockout — `PodPerRunResumabilityProbe`). A single-role
rejection now self-heals with a same-author revision instead of parking at a human on round one.

**Advisory** is a surfaced no-op that continues the gate loop without resetting work. **In-place steering**
is a separate executable decision and can apply to rejected work when resumable. Source/severity describes
the feedback, but direction determines whether revision stays in place or attempts rotation.

### RAI verdict contract: a machine-readable sentinel, not prose (#231)

The RAI gate that fronts the software assembly path has the same "structured output, not heuristics"
requirement as the #223 `TARGET_FILES:` hint — and for the same reason. **Before #231**, the RAI verdict was
recovered by scanning *every* line of the reviewer's response and treating any line that *started with* a
verdict token as a vote, then taking the max severity. That heuristic could not tell a decision from prose:
a legend echo (`- RED — critical violation that must block shipping…`) or a section header (`RED: none`)
counted as a `RED` vote, so a perfectly benign review **false-escalated to RED** and dead-ended the run at a
blocking content-safety failure.

The fix replaces the prose scan with a single machine-readable sentinel:

- **Prompt contract.** The RAI reviewer prompt now requires the **last line** of its response to be exactly
  `` `VERDICT: <GREEN|YELLOW|REVISE|RED>` `` and states plainly that *only* that line is read as the
  decision — the human-readable explanation (including the verdict legend) is never parsed for the verdict.
- **Sentinel-only parse.** `TryParseVerdict` consults `TryParseSentinelVerdict` first: it matches only lines
  that begin with the `VERDICT:` keyword (after stripping bullet/`**` markers). When multiple sentinel lines
  are present the **last one wins**; if a single sentinel line names more than one token the **most-severe
  wins**. Because the sentinel is authoritative, prose is *not* scanned at all — so a legend echo or a
  `RED: none` header can no longer produce any verdict, and **prose can never yield a passing verdict**.
- **Emoji fallback only.** The single non-sentinel fallback is an unambiguous verdict emoji
  (`TryParseEmojiVerdict`): 🔴 escalates to a blocking `RED`, 🟡 is an advisory (non-blocking) `YELLOW`.
  Nothing else in the prose is consulted.
- **One bounded re-ask, then fail safe to RED.** If neither a sentinel nor an emoji is found, `HandleAsync`
  issues exactly **one** bounded re-ask (`ReAskPrompt`) demanding *only* the sentinel line. If the verdict is
  **still** unparseable, the executor **fails safe to a blocking `RED`** (reason `unparseable_after_reask`,
  emitted as `run.rai_error`) rather than silently passing a `YELLOW`/`GREEN` — a rare, visible, recoverable
  false-block is strictly preferable to shipping a real `RED` (credentials, PII, harmful content).
- **A genuine RED stops autonomous progress, not the coordinator's recoverability.** Sentinel RED or 🔴
  sets `ContentSafetyFlagged`; `RunRaiAsync` surfaces `SafetyFlagged`. Collective assembly calls
  `ParkRaiRedAtHumanReviewAsync`, persists the reviewed branch/tree, and waits in `in_review` /
  `awaiting_review` with `reason: rai_red` for approve, request-changes, or decline. It does **not**
  call a terminal `RaiBlockAsync` (`CoordinatorAssemblyService.cs:1131–1136`, `:3757–3795`).
- **REVISE is distinct from RED.** It surfaces `RevisionRequested` and routes through explicit steering.
  A resumable target can revise in place; a fresh-dispatch decision attempts scoped rotation; exhausted
  autonomy (`Proceed`) durably escalates to human review instead of terminal `RaiBlocked`
  (`CollectiveAssemblyPipeline.cs:133–136`; `CoordinatorAssemblyService.cs:1139–1146`, `:2372–2388`).
  These are collective-assembly semantics, not a claim that standalone workflow RAI terminal edges changed.
- The surfaced rationale/feedback (`ExtractRationale` / `ExtractFeedback`) deliberately **skip** the sentinel
  line, so the human sees the explanation instead of a degenerate `VERDICT: REVISE`.

## Source

| Concern | File |
|---|---|
| Budget-exhausted escalation, `EscalateToHumanReviewAsync`, `ParkAtHumanReviewAsync` | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| `BuildAccumulatedGateFeedbackAsync`, human round-trip wiring, `IsEscalationDurablyOpenAsync` | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| #223 scoped re-dispatch: `RequestChangesAsync`, `RouteAssemblyGateThroughSteeringAsync`, `RedispatchDependentsAsync`, `EmitImplicatedScopeFallback` | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| #233 lockout rotation + single-eligible degrade-vs-escalate split: `ExecuteLockoutRotationAsync` (`if (deadlocked)` → `if (hasContext)` degrade branch at `:2218`; `lockout_no_context` escalation at `:2243`) | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2106` |
| #233 same-author fresh re-dispatch executor `ConsciousDispatchFreshFallbackAsync` (rationale `single_eligible_agent`) | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2883` |
| #233 recovery-budget bound: `CoordinatorSteeringDecider.DecideAsync` → `SteeringPolicy.Decide` (over-budget → `Proceed`); `MaxRecoveryAttempts` = 3 | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:119`, `:38`; `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs:1049` |
| #226 steer-at-review-gate delivery: `SteerAsync`, `TryDeliverAtAssemblyReviewGateAsync`, `DeliverAdvisorySendAtReviewGateAsync`, `ResolveTargetFilesForChildAsync` | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs` |
| #226 shared review-decision delivery `DeliverDecisionAsync`; `SteeringStatus.Deferred` | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyReviewPersistence.cs`; `CoordinatorSteeringService.cs` |
| #226 `/steer` `201`/`202` (deferred) response mapping | `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs` |
| #223 implicated scoping: `ScopeImplicatedSubtasks`, `TransitiveDependents` | `apps/Agentweaver.Api/Coordinator/AssemblyPlanning.cs` |
| #223 structured `TARGET_FILES:` parser `ReviewTargetFiles.Parse` / `IsDirectiveLine` | `packages/Agentweaver.AgentRuntime/Workflow/ReviewTargetFiles.cs` |
| #231 RAI verdict sentinel prompt + one-re-ask → fail-safe-RED flow `HandleAsync`, `ReAskPrompt`, reason `unparseable_after_reask` | `packages/Agentweaver.AgentRuntime/Workflow/RaiTurnExecutor.cs` |
| #231 sentinel-only parse `TryParseVerdict` / `TryParseSentinelVerdict` / `TryParseSentinelLine` / `TryParseEmojiVerdict`; sentinel-skipping `ExtractRationale` / `ExtractFeedback` | `packages/Agentweaver.AgentRuntime/Workflow/RaiTurnExecutor.cs` |
| Verdict consumption: `RunRaiAsync` → `SafetyFlagged` / `RevisionRequested`; RED → durable human review, REVISE → steering | `apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:133–136`; `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1131–1146`, `:3757–3795` |
| Reviewer `TARGET_FILES:` emission | `packages/Agentweaver.AgentRuntime/Workflow/RubberduckTurnExecutor.cs`; `BuildTestTurnExecutor.cs` |
| `coordinator.assembly_implicated_scope_fallback` event constant | `packages/Agentweaver.Domain/EventTypes.cs` |
| `TryEscalateToInReviewAsync`, `IncrementHumanReviewRoundTripAsync` | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyStore.cs` |
| Unconditional human-review budget reset (no cap), `ResetSteeringBudgetAsync`, `BuildRationale` | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs` |
| `HumanReviewRoundTrips` column (telemetry only); migrations | `apps/Agentweaver.Api.Data/Memory/WorkPlan.cs`; `migrations/20260710004451_AddHumanReviewRoundTrips.cs` |
| Stale lock recovery `ClearStaleIndexLock`, `ResolveGitDir`, `IsAnyGitProcessRunning` | `apps/Agentweaver.Api/Git/WorktreeManager.cs` |
| `TryClearStaleIndexLock` seam | `packages/Agentweaver.AgentRuntime/Workflow/IWorktreeOperations.cs`; `apps/Agentweaver.Api/Runs/WorktreeOperationsAdapter.cs` |
| `AgentTurnOutput.TerminalFailureReason/Evidence`; `ChildTurnFailedOutput` | `packages/Agentweaver.AgentRuntime/Workflow/WorkflowMessages.cs` |
| Child graph failure→terminal edge; `child-turn-failed` terminal binding | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` |
| `ChildTurnFailedOutput` → Failed(reason) in watch loop | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs` |
| `StartChildRevisionHandoffAsync` (lockout context-carrying handoff) | `apps/Agentweaver.Api/Runs/RunOrchestrator.cs` |
| `Subtask.LockedOutAgents` (existing dormant column, now used) | `apps/Agentweaver.Api.Data/Memory/Subtask.cs` |
| `CommitChangesWithRetryAsync` stale-lock clear between attempts | `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs` |

## See also

- [Resilient assembly-review — Reference](../reference/resilient-assembly-review.md) — config keys and operational knobs.
- [Resilient assembly-review — User Guide](../experience/resilient-assembly-review.md) — what operators and users observe.
- [Unified autonomous steering](./unified-steering.md) — the `SteeringSignal` and `CoordinatorSteeringDecider` that route assembly feedback.
- [Coordinator internals](./coordinator-internals.md) — the broader assembly pipeline and collective assembly stage.
- [Review & merge](./review-merge.md) — the human-review gate mechanics Fix-B escalates into.

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
