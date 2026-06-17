# Plan: Squad Coordinator Agent (Spec 008)

**Branch**: `008-coordinator-agent`
**Spec**: `specs/008-coordinator-agent/spec.md`
**Date**: 2026-06-17
**Supporting design**: `specs/008-coordinator-agent/data-model.md`

## Goal

Add a single new thing on top of the existing single-agent platform: an **orchestration
layer**. Every team gains a built-in **Coordinator** agent (codename Squad) that turns a
user goal into a confirmed, memory-informed **outcome spec**, decomposes it into a **work
plan**, and drives a team of existing single-agent runs to one combined result.

The coordinator is itself a MAF run (observable, streamed, human-accountable). It launches
each subagent as a **first-class child run**. A child run reuses the existing agent loop and
RAI gate but runs a **trimmed pipeline** that terminates at an *assemble-ready* state after RAI
— it does **not** run its own individual human review, merge, or scribe. This is a real change
to how the workflow graph is built (see B1 below): today `RunWorkflowFactory.BuildWorkflow`
hardwires `agent -> RAI -> review -> merge -> scribe` for every run, so a child variant that
stops after RAI is new wiring, not "reuse unchanged." The **single second human review, merge,
and scribe run exactly once** over the assembled collective output, and they are owned by the
**parent coordinator run** (re-wired into the coordinator workflow over a real integration
worktree), not by any child. The coordinator never does domain work — it only orchestrates and
persists the outcome spec / work plan into the existing Feature 006 memory store.

> **Why this changed (rubber-duck B1/B2/N1).** An earlier draft claimed children reuse the
> pipeline "unchanged" while deferring the review/merge/scribe short-circuit to Phase 3. That
> was contradictory and unshippable: every child started via `RunOrchestrator.StartRunAsync`
> traverses the whole `BuildWorkflow` graph, pauses at its **own** human review gate, and merges
> to the originating branch — so N children would mean N human gates and N merges, violating
> FR-021/SC-004. The trimmed child pipeline is therefore a **Phase 2 prerequisite of the first
> dispatch**, the collective review/merge/scribe is **new wiring in the coordinator workflow**
> (not free reuse), and real-time steering is **new infrastructure gated on a feasibility spike**
> (see Architecture Decisions and Phase 2).

This plan is a design artifact only. No product code is written here.

### Non-redundancy contract (the most important constraint)

The coordinator MUST NOT reimplement any platform capability. The table below is the
authoritative "reuse, do not rebuild" map; every design decision downstream honors it.

| Capability | Owned by | Coordinator does |
| --- | --- | --- |
| Agent loop / agent turn | Feature 001 (`RunWorkflowFactory`) | Reuses the agent + RAI executors inside a **trimmed child-run workflow variant** that terminates assemble-ready after RAI (new graph wiring, not the full graph) |
| RAI gate | `RaiTurnExecutor` (in 001 graph) | Reuses the executor per child; reads RAI findings off child runs and dispatches fixes |
| Human review / merge / scribe | 001 graph executors (`review-gate` RequestPort, `MergeExecutor`, `ScribeTurnExecutor`) | **Re-wires** the same executors into the coordinator workflow and runs them **once** over the assembled collective tree (new wiring + a new integration-merge step; see N1) |
| Sandbox / worktree isolation | Features 001/002 (`IWorktreeOperations`) | Chooses which strategy per subtask; assembles child worktrees into one integration tree |
| Casting / roster / per-role model | Feature 005 (`CastingService`) | Selects agent + model per subtask |
| Memory and decisions | Feature 006 (`MemoryDbContext`) | Reads context; persists outcome spec + work plan |

The platform-owned executors are reused as building blocks, but the **graph they are wired into
differs** for child runs (trimmed) and for the coordinator run (collective gate). "Reuse" means
reusing the executors, not reusing the existing single-run graph topology unchanged.

---

## Constitution Check

| Principle | How this design complies |
| --- | --- |
| I. Agent Runtime (MAF) | Coordinator is a MAF agent inside a MAF `Workflow`; fan-out/serialize is a MAF orchestration executor that dispatches child runs through the existing `RunWorkflowFactory` graph. No ad hoc loop. |
| II. Model Sources (Copilot only) | Per-subtask/per-phase model selection only varies the *model id* within GitHub Copilot, honoring the role default with a runtime override (`ModelSource` provider stays Copilot). Provider never selectable. |
| III. API-First | Every new capability (submit orchestrated run, confirm/revise outcome spec, observe child runs, steer, bubble-up answer, collective review) is a backend endpoint; clients hold no orchestration logic. |
| IV. Two clients at parity | Every new endpoint gets an MCP tool (thin proxy) and a Web UI surface. Parity table in MCP + Web sections. The **dynamic topology view** is surfaced at Web + MCP parity (live graph in Web, the same `coordinator.topology` + `coordinator.*`/`subtask.*` stream over the MCP server's `run_watch`) — see the Web UI and MCP Compatibility sections. |
| V. Observable runs | Coordinator run and every child run stream their steps. New `coordinator.*` and `subtask.*` events extend the existing `sequence`-ordered envelope; one additive `coordinator.topology` event drives the live topology view. Read-only child timelines are the existing `/stream` endpoint. |
| VI. Local + cloud parity | New entities live in the same `scaffolder.db` via EF Core (same as Feature 006). Child-run fan-out uses in-process MAF orchestration plus the existing run store — no new infra, runs identically local and hosted. |
| VII. No mocks | Each phase is an independently shippable, fully-functional slice. Phase 2 ships the **trimmed child-run pipeline** (agent -> RAI -> assemble-ready terminal) plus real dispatch of real child runs — no stubbed dispatch, no fake subagents, and no child that silently runs its own review/merge. The collective review/merge/scribe is a later slice but Phase 2 already terminates children correctly so nothing downstream depends on fake behavior. |
| VIII. No emojis | All product surfaces (charter text, events, API payloads, Web UI) use plain text / Fluent icons only. |
| IX. Responsible AI | A named human stays accountable for the parent run and every child run; bubble-up routes clarifications and gated-action permissions to that human; all coordinator and subagent messages/tools/results are attributable in the stream + audit log. |
| X. Safe execution | Each child run keeps its own sandbox/worktree, step and time bounds, and terminal state. Gated/irreversible subagent actions block on human approval via bubble-up. Merge stays human-gated and runs once. |
| XI. MAF governance / telemetry | Policy, guardrails, and telemetry stay in the MAF runtime/governance layer that the child runs already use; the coordinator adds no parallel enforcement path. |

No principle is violated. Tensions (parent/child run-bounding, the new trimmed child pipeline,
the re-wired collective gate, and steering-as-new-infra) are tracked under **Complexity
Tracking** rather than as violations. The FR-018a/SC-003 steering-wording tension is flagged in
**Risks & Open Questions** for the coordinator to take back to the user.

---

## Architecture Decisions

| Decision | Choice | Rationale |
| --- | --- | --- |
| How the coordinator runs | A MAF agent hosted in its own run via the existing run pipeline; its "tools" are orchestration tools (decompose, dispatch child run, observe, steer, bubble-up resolve) | Principle I: build on MAF, the coordinator is an observable run like any other (FR-002). |
| Fan-out / serialize engine | A new MAF orchestration executor (`CoordinatorOrchestratorExecutor`) inside a coordinator `Workflow`, dispatching child runs and awaiting their terminal states; dependency DAG drives parallel vs serial | Keeps orchestration inside MAF governance (Principle XI), not ad hoc threads. |
| Subagent = child run | Each subtask becomes a real `Run` created through `RunOrchestrator.StartRunAsync`, tagged with `ParentRunId` = coordinator run, executed through a **trimmed child-run workflow variant** (agent -> RAI -> assemble-ready terminal) | FR-015: own step stream, own RAI, own sandbox, reusing the 001/002 executors but stopping before individual review/merge/scribe (B1). |
| Parent/child linkage | New nullable `Run.ParentRunId` + `Run.SubtaskId`; coordinator observes children via the existing `/api/runs/{id}/stream` (read-only) | Read-only timeline is just the existing SSE stream of the child run (FR-015, FR-017). |
| Child-run pipeline variant (B1) | Add a `BuildWorkflow` overload — `BuildChildWorkflow()` (or a `ParentRunId != null` branch inside `BuildWorkflow`) — that keeps `agentInputStorer -> agent -> RAI` and the existing **RAI revise loop**, then routes RAI's non-revision, diff-present output to a new `childAssembleReady` terminal executor instead of `reviewAdapter`. No `review-gate` RequestPort, no `MergeExecutor`, no `ScribeTurnExecutor` on the child path. The empty-diff no-op path still terminalizes. The child records its worktree branch + tree hash as assemble-ready output the coordinator later collects. | FR-021/SC-004: a child must NOT trigger its own human gate or merge-to-base; exactly one collective gate. This is a **Phase 2 prerequisite of the first dispatch**, not deferred. |
| Collective gate placement (N1) | The coordinator run is a **different workflow** (`CoordinatorOrchestratorExecutor`). It re-wires the platform `review-gate` RequestPort + `MergeExecutor` + `ScribeTurnExecutor` into the coordinator graph over a **real coordinator worktree** holding the assembled tree, and runs them once. Merging N child worktree branches into one integration tree and detecting conflicts BEFORE the gate is **new code** (`MergeCoordinator` today only does single-run lock + merge-to-base). | FR-019/FR-021/FR-031: per-stream RAI, single collective review/merge/scribe, conflicts caught before the human gate. Budgeted explicitly in Phase 3 + Complexity Tracking. |
| Steering channel (NEW infra, B2) | Steering is **new infrastructure**, not reuse. Today the only in-flight control is `RunWorkflowRegistry.Abandon -> Cts.Cancel()` (hard stop); there is no mid-turn redirect/amend/pause hook, and `StartRevisionAsync` only fires when a run is already PAUSED at its review gate. So: `stop` = Abandon/cancel (exists); `redirect`/`amend` = enqueue a `SteeringDirective` the child applies at its **next turn boundary** (queue, then inject a revised task turn — not mid-turn); `pause` = no current primitive, design a "hold before next turn" gate or descope. `POST /api/runs/{id}/steer` enqueues the directive; the coordinator relays it. **Gated on a feasibility spike against MAF's streaming/turn model (Phase 2, step 0).** | FR-018/FR-018a: honest about what is buildable now. See Risks/Open Questions for the FR-018a/SC-003 wording tension flagged back to the coordinator. |
| Bubble-up channel | Reuses the existing **tool-approval gate** (`IToolApprovalGate.WaitForApprovalAsync`, a real blocking suspend-until-decided gate with tests) but routes the request up to the parent coordinator, which surfaces it attributed to the originating subagent and relays the human answer back down | FR-024..FR-027: one place for the human, attributed, independent work continues. Shell-command approvals are a separate dependency (N2). |
| Outcome spec + work plan storage | New EF Core entities in the **existing** `MemoryDbContext` (`scaffolder.db`), written by the coordinator on the team's behalf | FR-003/FR-004a: persist into Feature 006's store, no parallel memory. |
| Isolation strategy decision | Coordinator picks worktree-per-independent-subtask vs serialized shared workspace from the dependency DAG, using existing `IWorktreeOperations` | FR-030/FR-031: decide *which* strategy, reuse existing primitives; detect conflicts at assembly. |
| Built-in provisioning (N3) | Coordinator must be added to **BOTH** lists: the `builtinRoles` array in `CastingService.ConfirmCastAsync` (~line 752, which adds the roster role) **and** the `builtins` array in `ProvisionBuiltinAgents` (~line 1014, `scribe/ralph/rai`, which writes the on-disk `.squad/agents/{name}/charter.md`). Adding it to only one would either skip the role or skip the charter file. | FR-001: every team gets a fully-provisioned coordinator (role + charter) automatically. |
| Built-in default model (N3) | The coordinator's planning model follows **whatever convention the existing built-ins resolve to** — today `builtinRoles` hardcodes `"claude-sonnet-4.6"` (~line 766). Use that same resolution, not a new value; provider stays GitHub Copilot (Principle II). If the built-in default later becomes config-driven, the coordinator follows it. | Principle II: provider fixed to Copilot; do not introduce a divergent model default. |

---

## Data Model

All new entities live in the **existing** `MemoryDbContext` (`apps/Scaffolder.Api/Memory/`),
persisted in `scaffolder.db` via `Microsoft.EntityFrameworkCore.Sqlite` — the same store as
Feature 006. Extending the existing context (rather than a new one) is deliberate: FR-003 /
FR-004a require the outcome spec and work plan to live in the team's memory/decision store,
not a parallel one. Full field-level detail is in `data-model.md`; summary below.

| Entity | Key fields | Purpose | Lives with |
| --- | --- | --- | --- |
| `OutcomeSpec` | `Id`, `ProjectId`, `CoordinatorRunId`, `Goal`, `DesiredOutcome`, `Scope`, `Assumptions`, `Status` (drafting/awaiting_confirmation/confirmed/declined), `ConfirmedBy`, timestamps | The confirmable restatement of the goal; gates dispatch (FR-006..FR-009) | `MemoryDbContext` |
| `WorkPlan` | `Id`, `OutcomeSpecId`, `ProjectId`, `CoordinatorRunId`, `IsolationSummary`, `IntegrationBranch` (assembled-tree branch, N1), `Status`, timestamps | The persisted decomposition subagents read from + the assembly target (FR-004a, FR-010, FR-031) | `MemoryDbContext` |
| `Subtask` | `Id`, `WorkPlanId`, `Title`, `Scope`, `AssignedAgent`, `SelectedModelId`, `Phase` (none/planning/execution/validation), `IsolationStrategy` (worktree/shared), `Status` (…/`assemble_ready`/…), `ChildRunId`, `LockedOutAgents` (CSV) | One unit of work; child runs the trimmed pipeline and terminates `assemble_ready` (FR-011..FR-013, FR-023, FR-028) | `MemoryDbContext` |
| `SubtaskDependency` | `SubtaskId`, `DependsOnSubtaskId` | The dependency DAG that drives parallel vs serial (FR-013, FR-014) | `MemoryDbContext` |
| `BubbleUpRequest` | `Id`, `CoordinatorRunId`, `ChildRunId`, `OriginatingAgent`, `Kind` (clarification/permission), `Prompt`, `Status` (pending/answered/denied), `Answer`, `AnsweredBy`, timestamps | A subagent question / gated-action permission routed to the human (FR-024..FR-027) | `MemoryDbContext` |
| `SteeringDirective` | `Id`, `CoordinatorRunId`, `TargetChildRunId` (nullable = broadcast), `Kind` (redirect/pause/stop/amend), `Instruction`, `Status` (pending/queued/relayed/applied), `CreatedBy`, timestamps | A directive applied at the child's next turn boundary (stop=cancel; pause per spike) (FR-018/FR-018a, B2) | `MemoryDbContext` |

**Run linkage (Domain change):** add two nullable fields to `Scaffolder.Domain.Run` and the
run store: `ParentRunId` (the coordinator run) and `SubtaskId`. Backward compatible — null for
all existing single-agent runs. The coordinator run itself has `ParentRunId == null` and an
`AgentName` of the built-in coordinator.

**Migrations:** add one EF Core migration to `MemoryDbContext` for the six new tables and the
`Run` store change (the run store is the legacy raw `SqliteDb` — add the two nullable columns
there with an idempotent `ALTER TABLE ... ADD COLUMN` guard, consistent with how 005/006 added
columns). No data backfill required.

---

## API Design (Principle III — every capability first lands here)

All under `/api`, bearer-auth, owner-scoped, consistent with existing run/team/memory routes.

| Method | Path | Purpose | Spec |
| --- | --- | --- | --- |
| `POST` | `/api/projects/{id}/orchestrations` | Start a coordinator run from a goal; creates the coordinator (parent) run and returns its `runId` | FR-001/FR-002, US1 |
| `GET` | `/api/runs/{id}/outcome-spec` | Get the current outcome spec for a coordinator run (drafting/awaiting/confirmed) | FR-006/FR-007 |
| `POST` | `/api/runs/{id}/outcome-spec/confirm` | Confirm the outcome spec; unblocks decomposition + dispatch | FR-008 |
| `POST` | `/api/runs/{id}/outcome-spec/revise` | Request changes to the spec; coordinator revises and re-presents, no dispatch | FR-009 |
| `GET` | `/api/runs/{id}/plan` | Get the persisted work plan (subtasks, assignments, models, deps, isolation) | FR-010..FR-013, FR-004a |
| `GET` | `/api/runs/{id}/children` | List child runs of a coordinator run with status + assigned agent (board data) | FR-015/FR-017 |
| `GET` | `/api/runs/{childId}/stream` | (existing) Read-only timeline of a child run — reused unchanged | FR-015 |
| `POST` | `/api/runs/{id}/steer` | Enqueue a steering directive (redirect/amend applied at the next turn boundary; stop = cancel; pause subject to spike), optionally targeting one child | FR-018/FR-018a |
| `GET` | `/api/runs/{id}/bubble-ups` | List pending/answered bubble-up requests for a coordinator run | FR-024 |
| `POST` | `/api/runs/{id}/bubble-ups/{bid}/answer` | Answer a clarification or grant/deny a permission; relayed to the originating subagent | FR-025/FR-026 |
| `POST` | `/api/runs/{id}/review` | (existing endpoint) Collective second human review, bound to the coordinator run over its assembled integration tree | FR-021/FR-022 |

Notes:
- `POST /api/runs/{id}/review` reuses the **existing** review endpoint, but for a coordinator
  run the `review-gate` RequestPort + merge + scribe are **re-wired into the coordinator
  workflow** over the assembled integration tree (N1) — the endpoint is reused, the graph it
  drives is the coordinator's, not a child's. The `WorkflowReviewDecision` verdict flows back to
  the coordinator orchestrator, which dispatches follow-up work on `request changes` (FR-022).
- `POST /api/runs/{id}/steer` returns 202 and enqueues a `SteeringDirective`. Honest semantics
  (see B2 / steering spike): `stop` cancels via the existing `RunWorkflowRegistry.Abandon` path;
  `redirect`/`amend` are **queued and applied at the child's next turn boundary** (a revised task
  turn is injected — there is no mid-turn interrupt today); `pause` has no current primitive and
  is contingent on the Phase 2 spike (hold-before-next-turn) or is descoped. The run object is
  never killed-and-recreated, so "without restarting the run" (FR-018a) holds for the run, but
  the directive is **not** guaranteed to take effect mid-turn. See the FR-018a/SC-003 wording
  note in Risks/Open Questions.
- Bubble-up gated-action permission (FR-025) is anchored on the implemented **tool-approval
  gate** (`IToolApprovalGate`, a real blocking suspend gate). Gated **shell** actions depend on
  the separate shell-approval path, which has an open dependency (N2 / T017-api); see notes.
- Bubble-up `answer` for a denied permission stops the dependent action and any subtasks that
  require it, while independent subtasks continue (FR-027, edge case).

---

## Events (extends `docs/reference/events.md` taxonomy; same envelope + `sequence`)

New event types. Parent and child events share the `runId`/`sequence` envelope; child events
carry `parentRunId` and `subtaskId` in their payload so a client can stitch the board together.

| Type | When it fires | Payload fields |
| --- | --- | --- |
| `coordinator.started` | Coordinator run begins | `goal` |
| `coordinator.outcome_spec` | An outcome-spec draft/revision is presented for confirmation | `specId`, `status`, `desiredOutcome`, `scope`, `assumptions` |
| `coordinator.outcome_spec.confirmed` | User confirms the spec | `specId`, `confirmedBy` |
| `coordinator.plan` | Work plan is decomposed and persisted | `planId`, `subtasks` (array of `{subtaskId, title, agent, model, phase, isolation, dependsOn}`) |
| `subtask.dispatched` | A child run is launched for a subtask | `subtaskId`, `childRunId`, `agent`, `model`, `isolation` |
| `subtask.progress` | Coordinator relays a progress checkpoint observed from a child timeline | `subtaskId`, `childRunId`, `status`, `summary` |
| `subtask.rai` | A child run's RAI finding flows back to the coordinator | `subtaskId`, `childRunId`, `flagged` (bool), `finding`, `dispatchedFixAgent` (on re-dispatch) |
| `subtask.completed` | A child run reaches a terminal state | `subtaskId`, `childRunId`, `status` |
| `coordinator.bubble_up` | A subagent question/permission surfaces to the human | `bubbleUpId`, `childRunId`, `originatingAgent`, `kind`, `prompt` |
| `coordinator.bubble_up.answered` | The human answers a bubble-up | `bubbleUpId`, `kind`, `granted` (permission only), `answer`, `answeredBy` |
| `coordinator.steering` | A steering directive is relayed to one/more children | `directiveId`, `targetChildRunId` (nullable), `kind`, `instruction` |
| `coordinator.assembling` | Coordinator assembles the collective output and runs conflict detection | `childRunIds`, `conflicts` (array of paths, empty if none) |
| `coordinator.collective_review` | The single collective review is requested over assembled output | `treeHash`, `requestId` |
| `coordinator.topology` *(additive — drives the live view)* | The live graph changes: emitted once as a full snapshot right after `coordinator.plan`, then as deltas whenever a node/edge/status changes (dispatch, progress, RAI/lockout, completion, conflict, phase-chain expansion, fix-dispatch) | `delta` (bool — false = full snapshot, true = changed subset), `nodes` (array of `{nodeId, kind (coordinator/child), subtaskId?, childRunId?, parentRunId, title, agent, model, phase, isolation, status (queued/running/rai/assemble_ready/completed/failed), lockedOut}`), `edges` (array of `{from, to, kind (dependency/dispatch)}`) |

Child runs continue to emit their existing `agent.*`, `tool.*`, `review.*`, `run.*` events on
their own stream; the coordinator only adds the linkage and orchestration events above.

**`coordinator.topology` is the only additive event (Web/MCP viz need).** Tank's `coordinator.plan`
+ `subtask.*` events already carry every detail (ids, role, model, phase, isolation, status, and
`dependsOn` edges), but they are a one-time decomposition snapshot plus per-subtask lifecycle deltas
— a client would have to **merge** them and **infer** the evolving node/edge set to draw a live graph
that grows over time (fix-dispatch children, phase-chained children, conflict edges). That inference
is client-side topology computation, which Principle III forbids. `coordinator.topology` makes the
backend emit the authoritative node/edge/status set instead, keeping both clients thin. It is a
**server-side projection of existing entities** (`Subtask` + `SubtaskDependency` + `Run.ParentRunId`/
`SubtaskId`), so **no new persisted field and no `data-model.md` change is required** — `lockedOut`
derives from `Subtask.LockedOutAgents`, and `nodeId` is the stable `Subtask.Id`. It reuses the same
`runId`/`sequence` envelope and is ordered/deduplicated by `sequence` like every other event
(Principle V). **Late-join replay:** the coordinator run's event stream replays from `sequence` 0 on
every (re)connect, so a late-arriving or reconnecting client receives the original full
`coordinator.topology` snapshot plus all subsequent deltas and can reconstruct the graph from scratch
(Principle V graceful degradation).

---

## MCP Compatibility (Principle IV — parity; MCP stays a thin proxy)

New tools, one per new endpoint, in the `specs/007-mcp-server/plan.md` tool style. Added to a
new `Tools/OrchestrationTools.cs` group in `apps/Scaffolder.Mcp`. No business logic.

| Tool | Method + Path |
| --- | --- |
| `orchestration_submit` | `POST /api/projects/{id}/orchestrations` |
| `orchestration_outcome_spec_get` | `GET /api/runs/{id}/outcome-spec` |
| `orchestration_outcome_spec_confirm` | `POST /api/runs/{id}/outcome-spec/confirm` |
| `orchestration_outcome_spec_revise` | `POST /api/runs/{id}/outcome-spec/revise` |
| `orchestration_plan_get` | `GET /api/runs/{id}/plan` |
| `orchestration_children` | `GET /api/runs/{id}/children` |
| `orchestration_steer` | `POST /api/runs/{id}/steer` |
| `orchestration_bubble_ups` | `GET /api/runs/{id}/bubble-ups` |
| `orchestration_bubble_up_answer` | `POST /api/runs/{id}/bubble-ups/{bid}/answer` |
| `orchestration_review` | `POST /api/runs/{id}/review` (reuses existing `run_review`) |

`run_watch` (existing) already streams any run id, so observing the coordinator run and each
child run needs no new tool — the AI client calls `run_watch` on the coordinator run id to receive
the live topology (`coordinator.topology` + `coordinator.*`/`subtask.*` in `sequence` order) and
`run_watch` per child id from `orchestration_children`. Optionally, an additive
`orchestration_topology` tool (`GET /api/runs/{id}/topology`) exposes the same server-side projection
as a point-in-time snapshot for hosts that prefer a query over a stream; it adds no business logic.

---

## Web UI (Principle IV parity — React 19 + Fluent 2, no emojis, Fluent icons)

New surfaces in `apps/web` (parity with the API capabilities):

1. **Outcome-spec review panel** — shows the drafted desired outcome, scope, assumptions, and
   any scoped clarifying questions; Confirm / Request-changes actions
   (`/outcome-spec/confirm` and `/revise`). Dispatch is blocked until confirm (US1).
2. **Multi-agent run board** — realized as the **Dynamic Workflow View** (item 6 below): the
   live coordinator topology graph is the primary surface, and each child node expands to the
   existing read-only run timeline component fed by that child's `/stream`. Live status,
   assigned agent, model, phase, isolation badge (US2, US6).
3. **Steering controls** — per-child redirect/stop and a coordinator-level amend box that posts
   to `/steer` (US2, FR-018a). UI surfaces honest state: `stop` is immediate (cancel);
   `redirect`/`amend` are shown as "queued — applies at next turn"; `pause` is shown only if the
   Phase 2 spike confirms a primitive, otherwise omitted.
4. **Bubble-up inbox** — a single list of pending clarifications and permission requests, each
   attributed to its originating subagent; answer / grant / deny posts to `/bubble-ups/{bid}/answer`
   (US4). Independent subtasks visibly keep running.
5. **Collective review panel** — exactly one review surface over the assembled output, reusing
   the existing run-review component bound to the parent coordinator run (US3).
6. **Dynamic Workflow View Visualization** — a live, evolving **topology graph** of the
   coordinator run and its child runs that **replaces the fixed `agent -> rai -> review ->
   merge -> scribe` pipeline bar** for coordinator runs. The topology is dynamic (one parent
   root + N child nodes that appear, run, and complete over time, with parallel fan-out and
   serialized dependency edges), so it must render live as it evolves, not as a static pipeline
   (US2, US3, US6). Detailed below.

All status/iconography uses Fluent 2 icons; no emojis anywhere (Principle VIII).

### Dynamic Workflow View Visualization (live topology)

This is the visualization counterpart of the orchestration already planned above — additive, not
a redesign of the backend. It renders the live coordinator topology from the parent run's event
stream; the client stays **thin** (Principle III).

- **Reuse the existing graph stack, not the existing topology.** Today
  `apps/web/src/pages/WorkflowRunPage.tsx` already renders a React Flow (`@xyflow/react`) graph
  laid out by dagre (`utils/dagLayout.ts`, `layoutDag`/`NODE_W`/`NODE_H`) with a custom Fluent
  node (`WorkflowNode`) and a reusable `StatusBadge`. The dynamic view **reuses that stack** but
  swaps the **hardcoded** `EXECUTORS` / `FORWARD_EDGES` / `LOOPBACK_EDGES` constants
  (the `agent -> rai -> review -> merge -> scribe` pipeline) for a node/edge set that is **built
  entirely from backend events**. No new graphing dependency is introduced.
- **Coordinator-run detection.** The same route (`/projects/:projectId/runs/:runId/workflow`)
  detects a coordinator run (`Run.ParentRunId == null` and the run's agent is the built-in
  coordinator) and mounts the dynamic topology renderer; ordinary single-agent runs keep the
  existing static pipeline view unchanged. No regression to existing runs.
- **Nodes.** Root node = the coordinator run. Child nodes = one per subtask
  (`nodeId = subtaskId`, stable from plan time before `childRunId` exists). Each child node reuses
  the existing Fluent card pattern: `AgentAvatar` for the assigned roster agent, the `SelectedModelId`
  (model), a phase badge (planning/execution/validation), an isolation badge (worktree/shared), and a
  status badge for `queued | running | rai | assemble_ready | completed | failed | locked_out`
  rendered with `@fluentui/react-icons` (e.g. `CircleRegular`/`ArrowSyncRegular`/`ShieldRegular`/
  `CheckmarkCircleRegular`/`DismissCircleRegular`/`LockClosedRegular`). No emoji.
- **Edges.** Dependency edges (`SubtaskDependency`) render as forward edges showing serialized
  ordering; the coordinator-to-child dispatch relationship renders as a fan-out edge from the root.
  Siblings with no dependency edge are laid out **concurrently** by dagre, so a parallel fan-out is
  visibly parallel and a serialized chain is visibly sequential.
- **Live updates are 100% event-driven and thin (Principle III).** The view subscribes to the
  **parent** coordinator run's `/api/runs/{coordinatorRunId}/stream` and renders the node/edge/status
  set the backend describes. It consumes Tank's existing taxonomy — `coordinator.started`,
  `coordinator.plan`, `subtask.dispatched`, `subtask.progress`, `subtask.rai`, `subtask.completed`,
  `coordinator.steering`, `coordinator.bubble_up[.answered]`, `coordinator.assembling`,
  `coordinator.collective_review` — plus the one **additive** `coordinator.topology` event (snapshot
  + deltas; see Events) that carries the authoritative live node/edge/status set. The client does
  **no** topology computation beyond mapping an event status string to a Fluent badge/icon: it does
  not infer edges, merge plan + dispatch events into a graph, or derive lockout client-side. All of
  that is described by the backend in `sequence` order (Principle V).
- **Collapse / expand drill-in.** A child node collapses to a status card and expands to that
  child's **own step timeline** by mounting the existing `Timeline` over
  `/api/runs/{childId}/stream` (reusing `useRunStream` + `Timeline` unchanged). The child's trimmed
  pipeline (`agent -> RAI -> assemble-ready`) renders with the existing timeline components, in an
  expandable panel/modal — no new timeline code.
- **Inline controls.** Steering controls live **on the relevant node** (per-child redirect/stop; a
  coordinator-level amend at the root) posting to `/steer`, surfacing honest state (`stop` immediate;
  `redirect`/`amend` shown as "queued - applies at next turn"; `pause` only if the Phase 2 spike
  confirms a primitive). Bubble-up prompts appear inline on the **originating child node** (attributed
  to its subagent) and in the root inbox; the single collective review surfaces at the **root** node
  over the assembled tree.
- **Accessibility.** The graph container exposes a keyboard-navigable structure (focusable nodes,
  arrow-key movement between nodes, visible focus rings), each node carries an `aria-label`
  describing agent/role/model/status, and an `aria-live="polite"` region announces status
  transitions (consistent with the existing `Timeline` `role="log"`/`aria-live` pattern). Honors
  reduced-motion for edge/loop animations.

---

## MCP Parity (Principle IV — the live topology over the MCP server)

The dynamic topology is reachable at **Web + MCP parity**: anything the Web graph shows is available
to an MCP host through the existing run stream. The MCP server stays a **thin proxy** and computes no
topology of its own (Principle III).

- **Stream parity (already satisfied).** The existing `run_watch` tool streams any run id's
  `sequence`-ordered events. Pointed at the coordinator run id (from `orchestration_submit` /
  `orchestration_children`), it delivers the same `coordinator.*` / `subtask.*` events the Web view
  consumes — including the additive `coordinator.topology` snapshot + deltas that carry the
  authoritative live node/edge/status set. An MCP host therefore reconstructs the exact same live
  graph the Web view renders, with no new tool and no client-side topology computation. Because the
  coordinator stream replays from `sequence` 0 on (re)connect, a late-joining host still receives the
  original snapshot plus all deltas (Principle V).
- **Child drill-in.** The host calls `run_watch` per child run id (from `orchestration_children`) to
  follow a single child's own trimmed `agent -> RAI -> assemble-ready` step stream — the MCP
  equivalent of the Web expand.
- **Optional snapshot tool.** For hosts that prefer a point-in-time query over a stream, an optional
  `orchestration_topology` tool (`GET /api/runs/{coordinatorRunId}/topology`) may expose the same
  **server-side projection** as the `coordinator.topology` event — a single current node/edge/status
  snapshot. It is additive, grouped with the other orchestration tools, adds no business logic, and
  reuses the existing projection. Documented under the orchestration tools group in
  `docs/reference/mcp.md`.
- **Events.** Consumes exactly the events the Web view consumes (`coordinator.*`, `subtask.*`, and
  the additive `coordinator.topology`), in `sequence` order (Principle V). Local + cloud parity holds
  because both clients read the same stream (Principle VI).

---

## Coordinator Charter / Prompt

- **Provisioning (two lists — N3):** the coordinator must be registered in **both** provisioning
  lists, or it will be half-provisioned:
  1. Add `Coordinator` (display name "Squad") to the `builtinRoles` array in
     `CastingService.ConfirmCastAsync` (~line 752, alongside Scribe/Ralph/Rai) so the roster role
     exists at cast-confirm time (FR-001).
  2. Add `("coordinator", "Coordinator")` to the `builtins` array in `ProvisionBuiltinAgents`
     (~line 1014, currently `scribe/ralph/rai`) so its `.squad/agents/coordinator/charter.md` is
     actually written to disk.
  It is **not** written to `.github/agents/` (built-ins are internal). Note: today `squad.agent.md`
  is described as the only MAF agent file; the coordinator is a built-in addressed via its charter,
  consistent with the other built-ins.
- **Charter source:** a new embedded template
  `packages/Scaffolder.Squad/Templates/coordinator-charter.md`, seeded by the charter compiler.
  It is adapted from the reference Squad coordinator governance but with **all platform-provided
  behaviors stripped** — it does NOT re-specify RAI checks, casting, memory governance,
  sandboxing, review, merge, or scribe (FR-003). The charter describes only: read memories /
  decisions for context; draft and confirm an outcome spec; decompose into a work plan; select
  roster agent + model + isolation per subtask; dispatch child runs and observe their read-only
  timelines; relay steering; bubble up questions/permissions; assemble the collective output;
  hand off to the single collective review/merge/scribe; never do domain work itself (FR-004).
- **Default model (N3):** follow whatever convention the existing built-ins resolve to. Today
  `builtinRoles` hardcodes `"claude-sonnet-4.6"` (~line 766); the coordinator uses that same
  resolution rather than a new model id. Provider stays fixed to GitHub Copilot, overridable only
  within Copilot at runtime (Principle II).

---

## Documentation Plan

Update for Principle IV parity + the quality gate:

| File | Change |
| --- | --- |
| `docs/reference/api.md` | New "Orchestration" endpoint table (the 9 new routes) + note that `/review` doubles as the collective gate for coordinator runs |
| `docs/reference/events.md` | New `coordinator.*` / `subtask.*` event rows + the additive `coordinator.topology` event + the `parentRunId`/`subtaskId` linkage note |
| `docs/reference/mcp.md` | New "Orchestration" tool group + a note that `run_watch` on the coordinator run id streams the live topology (`coordinator.topology` + `coordinator.*`/`subtask.*` in `sequence` order) and the optional additive `orchestration_topology` snapshot tool |
| `docs/reference/web.md` | New sections for the outcome-spec panel, run board, steering, bubble-up inbox, collective review, and the **dynamic workflow view** (live coordinator topology graph: nodes, statuses, drill-in, inline controls, accessibility) |
| `docs/reference/coordinator.md` (new) | End-to-end reference: lifecycle (goal -> outcome spec -> work plan -> dispatch -> collective gate), parent/child model, steering and bubble-up semantics, isolation strategy |
| `docs/architecture/overview.md` | Add the orchestration layer to the architecture diagram/description |

---

## Implementation Phases (dependency-ordered, each independently shippable, no mocks)

### Phase 1 — P1: Outcome spec + confirm (US1)

Goal: real coordinator run that produces a confirmable, memory-informed outcome spec and blocks
dispatch until confirmed.

1. Domain: add `Run.ParentRunId` + `Run.SubtaskId` (nullable) and run-store columns.
2. EF Core: add `OutcomeSpec` entity + migration to `MemoryDbContext`.
3. Coordinator MAF agent + charter template + `builtinRoles` provisioning.
4. API: `POST /orchestrations`, `GET /outcome-spec`, `/confirm`, `/revise`.
5. Coordinator reads Feature 006 memories/decisions for context; drafts spec; emits
   `coordinator.outcome_spec`; persists `OutcomeSpec`.
6. MCP tools + Web outcome-spec panel + docs (api/events/mcp/web/coordinator).
Ships value alone: a structured, confirmable restatement; **zero dispatch before confirm**.

### Phase 2 — P1: Trimmed child pipeline, decompose, dispatch, observe, steer (US2)

Depends on Phase 1. This phase is the minimum viable coordinator and MUST be a correct,
no-mocks, independently shippable slice (Principle VII): real dispatch of real child runs that
terminate correctly assemble-ready, with no child running its own review/merge/scribe.

0. **Steering feasibility spike (gates step 6).** Short spike against MAF's streaming/turn model
   to determine what redirect/amend/pause can actually do today. Confirmed baseline: `stop` =
   `RunWorkflowRegistry.Abandon -> Cts.Cancel()`; `redirect`/`amend` realistically apply at the
   **next turn boundary** (queue a directive, inject a revised task turn — there is no mid-turn
   interrupt; `StartRevisionAsync` only fires when a run is paused at its review gate); `pause`
   has no current primitive. Output of the spike: either a "hold before next turn" design for
   `pause` or a decision to descope `pause` and flag the FR-018a/SC-003 wording (see Open
   Questions). Steering is **new infrastructure**, not reuse.
1. **Trimmed child-run pipeline (B1 — prerequisite of the first dispatch).** Add
   `BuildChildWorkflow()` (or a `ParentRunId != null` branch in `RunWorkflowFactory.BuildWorkflow`)
   that runs `agentInputStorer -> agent -> RAI` plus the existing RAI revise loop, then routes
   RAI's non-revision, diff-present output to a new `childAssembleReady` terminal executor
   instead of `reviewAdapter`. No `review-gate` RequestPort, no `MergeExecutor`, no
   `ScribeTurnExecutor` on the child path; empty-diff no-op still terminalizes. The child records
   its worktree branch + tree hash for the coordinator to collect. Without this, every child
   would hit its own human gate and merge-to-base, violating FR-021/SC-004.
2. EF Core: `WorkPlan`, `Subtask`, `SubtaskDependency`, `SteeringDirective` + migration.
3. `CoordinatorOrchestratorExecutor` (MAF): decompose confirmed spec into subtasks; select
   roster agent (Feature 005) + Copilot model per complexity; build dependency DAG; persist
   work plan (FR-004a).
4. Dispatch each subtask as a child run via `RunOrchestrator.StartRunAsync` tagged
   `ParentRunId`/`SubtaskId`, **using the trimmed child workflow**; run independent subtasks in
   parallel, dependent ones in order. Children terminate in an `assemble_ready` state.
5. Observe each child via existing `/stream`; emit `subtask.*` events; `GET /children`, `/plan`.
   Emit `coordinator.topology` (full snapshot at plan time, then deltas on every lifecycle
   transition) so the live topology view renders without any client-side topology computation.
6. Steering (built per the spike outcome): `POST /steer` enqueues a `SteeringDirective`; `stop`
   cancels; `redirect`/`amend` apply at the next turn boundary; `pause` per spike. Honest
   semantics documented; no claim of mid-turn interruption.
7. MCP tools + **Web dynamic topology view** (live coordinator+children graph that replaces the
   static pipeline bar for coordinator runs) + **MCP topology parity** (the same live graph is
   reconstructable via `run_watch` on the coordinator run id; optional `orchestration_topology`
   snapshot tool) + steering controls inline on nodes + docs. The Web view renders the graph thin
   from `coordinator.topology` + `coordinator.*`/`subtask.*` events (no client-side topology), and
   the MCP server proxies the same stream. Core live view (nodes, statuses, fan-out, drill-in,
   steering) lands here; richer dependency-edge/isolation rendering extends in Phases 3/6.
Ships the minimum viable coordinator: confirm, then deliver assemble-ready child output that the
collective gate (Phase 3) reviews/merges once.

### Phase 3 — P2: Assemble + single collective RAI-driven review/merge/scribe (US3)

Depends on Phase 2 (the child pipeline already short-circuits before individual
review/merge/scribe in Phase 2, so this phase owns only the **collective** gate). This is
largely **new wiring**, not free reuse (N1).

1. **Coordinator-run worktree + integration merge (new code).** The coordinator run owns a real
   worktree holding the assembled tree. Combine the N child worktree branches into one
   integration tree — `MergeCoordinator` today only does single-run lock + merge-to-base, so the
   N-way assembly merge is new (`MergeCoordinator` extension or a new `IntegrationMergeService`).
2. **Pre-gate conflict detection (FR-031).** Detect potential merge conflicts BEFORE the human
   gate; on conflict, serialize a reconciliation pass (US6 edge case). Emit
   `coordinator.assembling` (with `conflicts`) + `coordinator.collective_review`.
3. Route RAI findings back (`subtask.rai`); dispatch fixes with reviewer-rejection lockout
   (a different agent revises) using `Subtask.LockedOutAgents` (FR-023).
4. **Re-wire the platform `review-gate` RequestPort + `MergeExecutor` + `ScribeTurnExecutor`
   into the coordinator workflow** and run them **once** over the assembled integration tree;
   verdict flows back to the coordinator, which dispatches follow-up on `request changes`
   (FR-021/FR-022).
5. **Topology view — assembly + collective-review state.** Extend the live view (Web graph + MCP
   stream) to render the root coordinator's `assembling`/`collective_review` states and any
   pre-gate conflict edges from `coordinator.assembling`/`coordinator.collective_review`
   (`coordinator.topology` deltas). The single collective review surfaces at the **root** node.
6. Docs update.

### Phase 4 — P2: Bubble-up (US4)

Depends on Phase 2 (independent of Phase 3).

1. EF Core: `BubbleUpRequest` + migration.
2. Extend the existing **tool-approval gate** (`IToolApprovalGate.WaitForApprovalAsync`, a real
   blocking suspend gate with tests) so a child run's gated tool call or clarification routes up
   to the parent coordinator instead of resolving locally.
3. API: `GET /bubble-ups`, `POST /bubble-ups/{bid}/answer`; events `coordinator.bubble_up[.answered]`.
4. Gated/irreversible **tool** actions block until answered; denied permission stops dependents,
   independent subtasks continue (FR-025/FR-027).
5. **Shell-command approval (N2) — scope note.** Gated SHELL actions (FR-025) depend on the
   shell-approval path, which is in an inconsistent state: `IShellApprovalStore` exists and
   `POST /api/runs/{id}/shell-approvals` + `/shell-denials` are wired in `Program.cs`, but the
   docs still mark T017-api Open and reference a differently named endpoint
   (`POST /api/runs/{id}/shell-approve`), warning that runs hitting a destructive command pause
   indefinitely (`docs/architecture/sandboxed-execution.md`, `docs/reference/sandbox-setup.md`).
   For this feature, **scope FR-025 bubble-up to the implemented tool-approval gate** and record
   the shell-approval bubble-up as a **dependency on resolving T017-api** (endpoint naming +
   wiring the store into the sandbox's blocking wait). Do not assert shell-approval reuse as
   already-working.
6. MCP tools + Web bubble-up inbox + docs.

### Phase 5 — P3: Phasing (US5)

Depends on Phase 2.

1. Use `Subtask.Phase` (planning/execution/validation) with per-phase Copilot model selection
   (FR-028/FR-029); chain phase outputs as ordered child runs.
2. Web board shows phase badges; docs update.

### Phase 6 — P3: Isolation, dependency, conflict awareness (US6)

Depends on Phase 2.

1. Coordinator chooses worktree-per-independent vs serialized-shared from the dependency DAG
   using existing `IWorktreeOperations` (FR-030).
2. Build on Phase 3's pre-gate conflict detection: add the smarter isolation-strategy decision
   so overlapping work is sequenced/isolated up front; reach the single review gate with zero
   unresolved conflicts (FR-031, SC-008).
3. **Topology view — rich dependency/isolation edges.** Extend the live view (Web graph + MCP
   stream) to render `SubtaskDependency` edges and the serialized-vs-parallel / worktree-vs-shared
   isolation ordering richly, via `coordinator.topology` edge metadata.
4. Docs update.

---

## Risks & Open Questions

- **Parent run-bounding vs long-lived orchestration.** A coordinator run may outlive a single
  child run's step/time bounds. Mitigation: bound the coordinator run by its own wall-clock and a
  max-children limit; child runs keep their own independent bounds (tracked in Complexity Tracking).
- **Steering reality vs FR-018a/SC-003 wording (B2 — flag back to coordinator).** There is no
  mid-turn interrupt for a live MAF agent turn today. The only in-flight control is
  `RunWorkflowRegistry.Abandon -> Cts.Cancel()` (hard stop); `StartRevisionAsync` only fires when
  a run is paused at its review gate. So `redirect`/`amend` can only realistically apply at the
  **next turn boundary** (queue + inject a revised task turn), and `pause` has **no current
  primitive**. The run is never killed-and-recreated, so "without restarting the run" (FR-018a,
  SC-003) is technically satisfied, but "real-time" steering that reaches the agent **mid-turn**
  is not buildable now. **Proposed spec adjustment to take back to the user:** soften FR-018a /
  SC-003 from "in real time ... reaches a running subagent" to "reaches a running subagent at its
  next turn boundary without restarting the run," and either descope `pause` or define it as a
  hold-before-next-turn gate. The Phase 2 spike confirms the achievable set before build. This is
  flagged, not silently contradicted.
- **Collective worktree assembly is new code (N1).** Combining N child worktrees into one
  integration tree and detecting conflicts before the gate is the novel mechanical step;
  `MergeCoordinator` today only does single-run lock + merge-to-base. Mitigation: extend
  `MergeCoordinator` (or add `IntegrationMergeService`) for the N-way assembly + pre-gate
  conflict detection; serialize a reconciliation pass on conflict (US6 edge case). Budgeted in
  Phase 3 and Complexity Tracking.
- **Open question:** does the outcome spec ever need to materialize as a committed `specs/NNN`
  directory? Spec assumes no (in-run artifact persisted to memory). Plan follows that assumption;
  revisit only if a clarification overrides it.

## Complexity Tracking

| Item | Why it adds complexity | Why it is justified / how bounded |
| --- | --- | --- |
| New `Run.ParentRunId`/`SubtaskId` + run-store columns | Touches the core run primitive | Minimal nullable additions, backward compatible; required for first-class child runs (FR-015) with no new run type |
| New trimmed child-run workflow variant (B1) | A second `BuildWorkflow` topology (agent -> RAI -> assemble-ready terminal) | Required by FR-021/SC-004 so children do not each trigger their own human gate + merge-to-base; reuses the same executors, only changes the edges; lands in Phase 2 as a dispatch prerequisite |
| Re-wired collective gate + N-way integration merge (N1) | Coordinator workflow re-wires review/merge/scribe and adds new N-way assembly + pre-gate conflict detection | `MergeCoordinator` only does single-run merge-to-base today; the collective gate is genuinely new wiring, not free reuse; bounded to Phase 3, runs exactly once (SC-004) |
| Steering as new infrastructure + spike (B2) | No mid-turn control primitive exists; redirect/amend/pause are new | Gated on a Phase 2 feasibility spike; honest semantics (next-turn-boundary apply, stop = cancel, pause per spike); FR-018a/SC-003 wording tension flagged to the coordinator |
| Coordinator run may outlive child bounds | A run that spawns runs blurs the single-run bound model (Principle X) | Coordinator gets its own explicit wall-clock + max-children bound; children keep independent bounds; both terminalize visibly |
| Six new EF Core entities in `MemoryDbContext` | Larger memory schema | Required by FR-003/FR-004a to keep outcome spec + work plan in the team's existing store, not a parallel one |
| Dynamic topology view + additive `coordinator.topology` event | A second workflow-view mode (live graph) and one new event | Reuses the existing React Flow/dagre stack (no new dep) and is a server-side projection of existing entities (no `data-model.md` change); the one additive event keeps Web + MCP clients thin (Principle III) instead of inferring an evolving graph client-side; lands in Phase 2, extends in Phases 3/6 |
