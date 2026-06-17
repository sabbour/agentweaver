# Steering Feasibility Spike (Phase 2, step 0 — gates step 6)

**Author:** Morpheus (Runtime/Workflow Engineer)
**Date:** 2026-06-17
**Status:** Complete — investigation + design recommendation. No steering code built (later wave).
**Question:** What can the four steering verbs — `redirect` / `amend` / `pause` / `stop` — actually
do today against MAF's streaming/turn model, so the Phase 2 steering implementation has honest
semantics (no false claim of mid-turn interruption)?

---

## TL;DR

| Verb | Buildable today? | Mechanism it builds on | Timing |
| --- | --- | --- | --- |
| `stop` | ✅ Yes (exists) | `RunWorkflowRegistry.Abandon` → `Cts.Cancel()` | Immediate (cancels the in-flight turn's `ct`) |
| `redirect` | ⚠️ New infra, feasible | Queue directive → inject a revised task turn (pattern: `RunOrchestrator.StartRevisionAsync` + `SendResponseAsync` resume seam) | Next turn boundary |
| `amend` | ⚠️ New infra, feasible | Same as `redirect` (revised-task injection) | Next turn boundary |
| `pause` | ❌ No primitive | None exists; would require a new "hold-before-next-turn" RequestPort gate | **Recommend descope for Phase 2** |

**Steering is NEW infrastructure, not reuse.** The only in-flight control primitive that exists
today is the hard cancel. Everything else is queue-and-apply-at-the-next-turn-boundary.

---

## 1. Confirmed mechanism per verb

### `stop` — confirmed: hard cancel (exists)

`stop` maps cleanly onto the existing cancellation path. There is exactly one per-run
`CancellationTokenSource`, created before the workflow starts and handed to both the workflow
execution and the registry:

- `RunOrchestrator` creates `runCts` and passes `runCts.Token` into `StartAsync`, then registers it:
  `apps/Scaffolder.Api/Runs/RunOrchestrator.cs:95-97` (also `:144-146`, `:204-206` for the revision/
  reserved paths).
- The registry owns the CTS and cancels it on abandon:
  `RunWorkflowRegistry.Abandon` → `pair.Cts.Cancel()` at
  `apps/Scaffolder.Api/Runs/RunWorkflowRegistry.cs:43-51` (cancel at `:48`). `Register` also cancels a
  replaced run's CTS at `:31-35`. `Remove` aliases `Abandon` at `:53`.
- That same `ct` flows all the way into the in-flight agent turn:
  `AgentTurnExecutor.HandleAsync(... ct)` → `_agent.ExecuteStreamingLoopAsync(input.Task, session, ct)`
  at `packages/Scaffolder.AgentRuntime/Workflow/AgentTurnExecutor.cs:40-41, 68`.

So `stop` is **immediate** and is the **only** control that reaches an agent **mid-turn** — but it is
a hard kill, not a graceful redirect. The watch loop treats the resulting `OperationCanceledException`
as an abandon, not a failure (`CoordinatorRunService.cs:217-220`).

`SteeringDirective(Kind=stop)` ⇒ resolve `TargetChildRunId` → `RunWorkflowRegistry.Abandon(childRunId)`.
Broadcast (`TargetChildRunId == null`) ⇒ `Abandon` each active child. No new runtime primitive needed.

### `redirect` / `amend` — confirmed: NO mid-turn interrupt; apply at next turn boundary

There is **no** API to interrupt or mutate an agent turn that is already executing inside
`ExecuteStreamingLoopAsync`. The token deltas stream out, but nothing streams *in* to redirect the
agent mid-turn. The realistic mechanism is **queue the directive, then inject a revised task turn at
the next boundary**, exactly mirroring the existing revision flow:

- `RunOrchestrator.StartRevisionAsync(Run run, string revisedTask, ...)` at
  `apps/Scaffolder.Api/Runs/RunOrchestrator.cs:158-208` builds a fresh `AgentTurnInput` with the
  revised task and `IsRevision: true` (`:184-200`) against the **same worktree/branch**, then starts a
  new streaming execution on the **same runId** (`:204-206`). The agent **resumes its session** rather
  than starting cold: `AgentTurnExecutor.cs:64-66` (`IsRevision ? ResumeSessionAsync : CreateSessionAsync`).
  This is the template for "inject a revised task turn" — the run is never killed-and-recreated.
- Crucially, `StartRevisionAsync` is only invoked **after the run has already paused at its review
  gate** and a human posts a decision. The suspend/resume seam is the MAF `RequestPort`:
  - `RunWorkflowFactory` builds `RequestPort.Create<WorkflowReviewRequest, WorkflowReviewDecision>("review-gate")`
    at `apps/Scaffolder.Api/Runs/RunWorkflowFactory.cs:206`. Suspension surfaces as a `RequestInfoEvent`
    on the stream (`RunWatchLoopService.cs:98`).
  - Resume happens via `streamingRun.SendResponseAsync(response)` — `Program.cs:800` for the run review
    gate; `CoordinatorRunService.cs:185-188` for the coordinator's own confirmation gate.

This confirms the plan's baseline: redirect/amend can only realistically take effect **at the next
turn boundary**, by queueing a `SteeringDirective` that the coordinator relays and the child applies as
a revised/extra task turn when its current turn completes (or when it next suspends at a gate). There
is no mid-turn interrupt today.

`SteeringDirective(Kind=redirect|amend)` ⇒ persist `pending` → coordinator relays (`queued`/`relayed`)
→ at the child's next turn boundary, inject the `Instruction` as a revised task turn (the
`StartRevisionAsync`-style "resume session + new task" pattern) → mark `applied`.

### `pause` — confirmed: NO current primitive

There is no "hold" primitive. The only points at which a run suspends are **statically-wired
`RequestPort` gates** that are part of the workflow graph topology (`review-gate`,
`coordinator-confirmation-gate`). You cannot dynamically suspend an arbitrary running turn or insert a
gate at runtime. A turn, once started, runs to completion or is hard-cancelled (`stop`). So `pause`
has no existing building block — it is genuinely new infrastructure.

For comparison, the coordinator's own confirmation gate suspend/resume (the primitive steering would
reuse) is: `RequestPort.Create<CoordinatorOutcomeSpecRequest, CoordinatorOutcomeSpecDecision>(
ConfirmationGateId)` at `apps/Scaffolder.Api/Coordinator/CoordinatorWorkflowFactory.cs:88, 102`,
suspended via `RequestInfoEvent` (`CoordinatorRunService.cs:236-242`) and resumed via
`SendResponseAsync` (`CoordinatorRunService.cs:185-188`).

---

## 2. Recommendation for `pause` — **DESCOPE for Phase 2**

**Recommendation: descope `pause` as a standalone, indefinite-hold verb in Phase 2.** Ship
`stop` + `redirect` + `amend` only. If a product owner rejects descoping, the concrete fallback design
is sketched below, but descope is the recommended call.

**Rationale (one line):** no hold primitive exists; a faithful "hold-before-next-turn" gate duplicates
most of the steering-channel infra (new RequestPort topology in every child graph + a directive flag
store + release/timeout/orphan-cleanup semantics) for marginal value over `redirect`/`amend`, which
already gate at the next turn boundary, while `stop` already gives an immediate halt.

Why the value is marginal: at the next turn boundary the child *already* consults queued directives
(that is how redirect/amend work). A `pause` would just be "hold at the boundary with no new
instruction, indefinitely" — bounded on one side by `stop` (immediate halt) and on the other by
`redirect`/`amend` (next-boundary new direction). It occupies a thin slice between two verbs we are
already shipping, but carries the highest relative complexity (indefinite suspension + release +
timeout + orphaned-pause cleanup + checkpoint interaction).

### ⚠️ FR-018a / SC-003 wording to flag back to the coordinator (Vesper/PM)

Descoping `pause` requires a spec wording fix — both requirements currently enumerate `pause` in the
"next turn boundary" promise:

- **FR-018** (`spec.md:175`): "redirect, **pause**, or stop it" — drop `pause` or footnote it as
  deferred.
- **FR-018a** (`spec.md:176`): "a redirect, amend, **or pause** MUST take effect at the targeted
  subagent's next turn boundary" — remove `pause` from this MUST, or restate `pause` as
  "stop + manual re-dispatch" so we do not promise an indefinite-hold primitive we are not building.
- **SC-003** (`spec.md:219`): "at the subagent's next turn boundary for a redirect/amend/**pause**" —
  same edit.
- User stories 5 & 6 (`spec.md:58-59`) and the prose at `spec.md:46, 232` likewise enumerate `pause`.

This is consistent with the tension already flagged in `plan.md:537-546` (soften "real-time …
reaches a running subagent" → "reaches a running subagent **at its next turn boundary** without
restarting the run", and "either descope `pause` or define it as a hold-before-next-turn gate"). This
spike resolves that open question in favor of **descope**.

### Fallback design (only if product insists on shipping `pause`)

A minimal "hold-before-next-turn" that reuses existing primitives:

1. **Where the check goes:** at each child turn boundary — i.e., the seam that today decides whether to
   inject the next/revised task turn (the `StartRevisionAsync`-style relay point). Before injecting the
   next turn, the relay reads the child's queued `SteeringDirective`s.
2. **How it holds:** if a `Kind=pause` directive is `queued`, route the child into a dedicated
   `RequestPort` "steering gate" (a new statically-wired gate in the child graph) instead of injecting
   the next turn. This suspends the run and emits a `RequestInfoEvent` — identical mechanics to the
   confirmation gate. The run is checkpointed and consumes no compute while held (no restart needed, so
   "without restarting the run" still holds).
3. **How it's released:** a subsequent directive resumes it via `SendResponseAsync` — `Kind=resume`
   (plain continue), or a `redirect`/`amend` (continue with new instruction), or `stop` (cancel). Add a
   pause **timeout** (auto-resume or auto-stop) to avoid orphaned indefinite suspensions, plus cleanup
   for a child paused when its parent is abandoned.

Cost: a new RequestPort in every child graph + directive-flag store + release/timeout/orphan handling —
i.e., it builds most of the steering channel just for `pause`. Hence the descope recommendation.

---

## 3. `SteeringDirective` lifecycle mapping (pending → queued → relayed → applied)

Status field per `data-model.md:108-118` / `plan.md:116`. Mapping each lifecycle state onto the
confirmed mechanisms:

| Status | What it means | `stop` | `redirect` / `amend` | `pause` (if not descoped) |
| --- | --- | --- | --- | --- |
| `pending` | `POST /api/runs/{id}/steer` persisted the directive (HTTP 202); not yet seen by the coordinator | row inserted | row inserted | row inserted |
| `queued` | Coordinator has picked it up and targeted child run(s) (`TargetChildRunId`, or broadcast if null) | resolved to child run id(s) | held for the child's next turn boundary | held for the child's next turn boundary |
| `relayed` | Directive handed to the target child's control seam | `Abandon(childRunId)` invoked → `Cts.Cancel()` | revised task turn staged for injection at next boundary | child routed toward the steering gate at next boundary |
| `applied` | Directive took effect | turn cancelled / run terminal (immediate) | revised/extra task turn executed (next boundary) | child suspended at steering gate; released by a later resume/redirect/stop |

Notes:
- `stop` collapses `relayed → applied` essentially instantly (cancellation is synchronous on the token);
  the others land `applied` only **at the next turn boundary**, so the UI must show `redirect`/`amend`
  as "queued — applies at next turn" (per `plan.md:251-253, 312-314`).
- Broadcast directives (`TargetChildRunId == null`) fan `queued → relayed` out to every active child.
- Honest-semantics guardrail: nothing here interrupts an in-flight turn except `stop`. Do not surface
  redirect/amend/pause as "immediate."

---

## Confirmed-vs-refuted summary

| Plan baseline assumption | Verdict |
| --- | --- |
| `stop` == `RunWorkflowRegistry.Abandon` → `Cts.Cancel()` | ✅ Confirmed (`RunWorkflowRegistry.cs:43-51`; token reaches the turn via `AgentTurnExecutor.cs:68`) |
| `redirect`/`amend` apply at the next turn boundary; no mid-turn interrupt | ✅ Confirmed (no inbound mid-turn channel; injection pattern = `StartRevisionAsync`, `RunOrchestrator.cs:158-208`) |
| `StartRevisionAsync` only fires when a run is paused at its review gate (RequestPort) | ✅ Confirmed (gate `RunWorkflowFactory.cs:206`; resume via `SendResponseAsync`, `Program.cs:800` / `CoordinatorRunService.cs:185-188`) |
| `pause` has no current primitive | ✅ Confirmed → **recommend descope for Phase 2** |
