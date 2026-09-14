## Scope and result

Read-only research completed against **`C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`**. No repository files were written, no subagents or external research services were used, and tests were **read, not executed**.

Below, `sabbour/agentweaver:path:lines` identifies files in that exact worktree, not the remote branch. The corrections remain prose/table/JSON except for the explicitly listed shared-diagram consumers.

Two implementation details require more precision than the audit rationale:

- SSE is **durably backed but still has a local-entry delivery path**; do not replace “memory-only” with “every request exclusively reads the database.”
- The selector’s fallback is not invariably the configured project default: `WorkflowSelector.ResolveDefault` prefers a `default`/`standard` candidate, then a non-`code-review` candidate. The outer orchestrator retains the resolved project default as its exception fallback.

---

# 1. `docs/reference/api.md`

## A. Replace blanket ownership claims with the actual role boundary

**Existing excerpts / locations**

- L118: “Use owner-scoped `/api/runs/{id}`…”
- L559: “Only the submitting user may access their own runs…”
- L626: “Requires a valid bearer key and run ownership…”
- L655: “Only the run owner may submit a decision.”
- L754: “Owner-scoped…”
- L1330: “All project endpoints are caller-owned… listing returns only that caller’s projects…”
- L1491: “owner-scoped like any other run.”
- L1557, 1589, 1601, 1627, 1673, 1711, 1757, 1800–1812 repeat “Owner-scoped.”

**Replacement contract**

> Persisted project-scoped runs inherit access from their stored project: Viewer permits inspection; Contributor permits run control, review, approval, questions, and steering; Owner permits project administration. Authorization uses the run’s persisted `ProjectId`, not caller-supplied project context or the submitting-user string. Older runs without a project retain submitting-user ownership; a dangling project reference fails closed. Unauthorized SSE requests for ordinary run IDs return `404` to avoid disclosing existence.

Apply **Viewer** wording to run detail/stream/graph/outcome-spec/work-plan/children/assembly-file inspection. Apply **Contributor** wording to confirm/revise/steer/assembly review and ordinary run controls.

At L1330 replace the paragraph with:

> Project listing and access are role-based. Creating a project establishes ownership; listing returns projects visible to the caller. Project inspection requires Viewer, orchestration and supported operational mutations require Contributor, and administration—including provider settings, role assignments, rename, and deletion—requires Owner.

Do **not** mechanically remove Owner requirements from provider settings, project administration, or memory trust promotion. Personal Assistant session APIs and personal-session deletion have their own caller-ownership rules.

**Evidence/tests**

- Persisted-run authorization and legacy fallback: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:115-153`.
- Personal Operator deletion exception: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:155-184`.
- Run Viewer and Contributor checks: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:46-75`, `:845-876`, `:1924-1973`.
- SSE existence-hiding behavior: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:426-454`.
- Coordinator role checks: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:147-148`, `:408-409`, `:487-488`, `:670-671`, `:761-762`.
- Project administration and orchestration: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:688-706`, `:788-801`, `:1453-1457`.
- Tests explicitly distinguish submitting identity from project authority and permit Viewer inspection: `sabbour/agentweaver:tests/Agentweaver.Tests/Auth/ProjectRunAuthorizationTests.cs:37-59`, `:291-330`.

## B. Replace memory-only SSE/restart prose

**Existing**

L643:

> “The server resumes from that point in the in-memory event buffer…”

L645:

> “After a process restart, the in-memory event history is lost…”

L2189:

> “The run’s event stream is held in memory by `RunStreamStore` and is not persisted to SQLite.”

**Replacement for L643–645**

> Set `Last-Event-ID` to the last per-run sequence received. Both configured database providers register durable event storage: SQLite uses `SqliteRunEventStream`; PostgreSQL uses `EfRunEventStream`. The SSE endpoint serves a retained local stream entry when available and otherwise uses durable replay-and-tail, including events written by another producer. Process restart or local-entry eviction does not itself erase persisted run-event history.
>
> Durable subscribers emit the complete loaded replay batch before terminating, so persisted diagnostics following a terminal event in that batch are delivered. `coordinator.assembly_blocked` is not a terminal stream event. A `done` frame ends that connection; a human gate or other parked state must not automatically be interpreted as a terminal run.

**Replacement for L2189**

> Run events are persisted through `IRunEventStream`; `RunStreamStore` also maintains local delivery state. Use `/events` for persisted event history and `/stream` for streaming/replay. `/history` exposes the separate persisted session-history surface. The single final-result `agent.message` fallback is legacy compatibility for completed runs lacking run-event rows, not the normal restart contract.

**Important limitation:** Do not promise local-entry delivery re-queries durable history on every poll. The endpoint’s local branch uses `GetSnapshotSince`; its no-local-entry branch uses `SubscribeAsync`.

**Evidence/tests**

- Provider registration: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1043-1053`.
- Two endpoint paths and legacy fallback: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:480-569`.
- Batch drain/terminal handling: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:144-190`.
- Cross-instance tail, late diagnostics, nonterminal block: `sabbour/agentweaver:tests/Agentweaver.Tests/EfRunEventStreamTests.cs:28-51`, `:74-118`, `:120-153`.
- SQLite restart/cursor tests: `sabbour/agentweaver:tests/Agentweaver.Tests/SqliteRunEventStreamTests.cs:56-92`.

## C. Correct graph contracts and examples

**Existing**

L779:

> “PLANNED Phase 3… It is shape-only — runtime status is NOT baked in…”

L783–784 hardcode four assembly nodes, a first RAI node, and exactly two loopbacks.

**Replacement**

> A coordinator descriptor combines work-plan topology with persisted coordinator/assembly state. Optional node fields include `status`, `status_reason`, and `terminal_stage`; subtask lifecycle updates also arrive through `subtask.*` and `coordinator.topology`.
>
> Assembly gate nodes are resolved from the selected workflow. Their stable `planned:assembly-*` IDs do not mean they remain planned: `kind` becomes `live` when the corresponding stage starts. Failure projection uses `assembly_terminal_stage` so a later failure-scribe pass does not make never-run gates appear executed. A delegated plan marks skipped assembly nodes `status: "delegated"` while leaving their kind planned.
>
> Leaf subtasks connect to the first resolved assembly gate—or merge if there are no gates. Resolved gates form a chain followed by merge and scribe. Every resolved gate has a coordinator loopback; loopbacks are excluded from forward fan-out/fan-in degree calculations.

Add the three optional status fields to L773’s field list. Label fixed node lists as **examples for a particular workflow**, not universal contracts.

**Evidence/tests**

- DTO fields: `sabbour/agentweaver:apps/Agentweaver.Api/Runs/Graph/GraphDescriptor.cs:30-45`.
- Runtime projection: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorGraphDescriptor.cs:165-225`.
- Dynamic edges and loopbacks: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorGraphDescriptor.cs:269-304`.
- Endpoint obtains selected gates: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:725-733`.
- Tests: `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorGraphDescriptorTests.cs:269-367`.

## D. Replace universal kubectl preview documentation

**Existing L1025–1051**

> “These endpoints… [run] `kubectl port-forward`…”
> “Starts a port-forward session from a random local port…”
> Request `{ "targetPort": 3000 }`.

**Replacement**

> These run-scoped endpoints start and manage browser previews. With `Sandbox:Preview:Enabled=true`, the API provisions Gateway-direct routing through a per-preview HTTPRoute and ClusterIP Service to the run’s sandbox pod and returns `preview_url` and `keepalive_url`. With preview disabled, the endpoint falls back to `kubectl port-forward` on the API host; that loopback port is not automatically reachable from a remote user’s browser. Viewer can list previews; Contributor can start or stop them.

Use the actual request spelling:

```json
{ "target_port": 3000 }
```

Retain the existing `pf-abc123` response **only under “Disabled-preview/local-development fallback.”** Add a Gateway response example using the emitted public fields. Distinguish initial `1–65535` validation from the enabled Gateway’s configured allowed-port range.

**Evidence**

- Route/body/role boundary: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:16-50`.
- Viewer list and branch-specific results: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:390-426`.
- Shared publication path: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:430-515`.
- Gateway routing/publication validation: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:200-220`, `:237-308`.

## E. Launch modes, status lists, and provider context

- L1506/L1515’s `define_outcome` is **accepted compatibility syntax**, not an invalid value. Prefer `defineOutcome` consistently and document `define_outcome` as accepted alias. Do not describe optional `start_mode` as a required request property.
- Clarify Direct: it skips model-drafted outcome definition and its confirmation gate, but persists a **confirmed prompt-backed spec** for the existing work-plan relationship.
- L1757 must describe **selected workflow automated gates before human review**, not universal “RAI, then human review.”
- L117, L578, L1662’s work-plan status inventories are incomplete if presented exhaustively. Include `delegated`, `assembly_steering`, `rai_blocked`, and `needs_resolution`, or explicitly label them common values. Do not classify `assembly_blocked` as inherently terminal.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:1530-1546`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:172-185`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:3013-3054`.

Keep the existing L105 AI-execution-context section and expand it in prose:

> The prepared `execution_key` authorizes the matching operation and scope and is checked against caller, expiry, and effective-provider identity. A provider fingerprint is comparison/provenance data, not an authorization credential. Accepted execution context is revalidated before model invocation; run provider snapshots and run-scoped capabilities remain separate boundaries.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Auth/AiExecutionPlanService.cs:293-360`, `:384-411`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/EffectiveModelProviderProvenance.cs:116-147`.

**No new provider-context asset or concept ownership is authorized by this plan.**

---

# 2. `docs/reference/coordinator.md`

## A. Replace Phase-1-only and unconditional human-confirmation text

**Existing locations**

- L3: coordinator creates a confirmed outcome “before any work begins.”
- L27/L32: casting/decomposition/dispatch described as “later phase(s).”
- L36/L40/L42: always draft, always suspend, terminate on confirmation.
- L59: “no subagent work… before a human confirms.”
- L70: “No work begins before confirmation…”

**Replacement section**

> Coordinator launch supports two modes:
>
> | Mode | Initial behavior | Dispatch boundary |
> |---|---|---|
> | `defineOutcome` | Draft and persist an outcome spec. With Autopilot off, wait for confirm or revise. | Confirmation advances into workflow selection, decomposition, dispatch, observation, steering, and collective assembly. |
> | `defineOutcome` with launch Autopilot | Draft the spec, then use the normal confirmation seam unattended on behalf of the accountable submitting user. | Continues into the same orchestration pipeline. |
> | `direct` | Persist a confirmed prompt-backed spec without a model-drafted outcome or confirmation RequestPort. | Plan and dispatch directly from the goal. |
>
> Direct mode and Autopilot do not remove workflow review/merge requirements or grant arbitrary tool permissions. Outcome confirmation is not itself orchestration completion. Runs with dispatchable subtasks remain active through dispatch and collective assembly; zero-inline/delegated outcomes can finalize separately.

Rename the heading to **“Launch modes and outcome definition”**, retaining an explicit compatibility anchor if any existing consumers rely on `#the-phase-1-outcome-spec-flow`.

Evidence/tests: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:189-208`, `:1162-1194`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorOutcomeSpecTests.cs:398-421`, `:555-584`.

## B. Replace the selection algorithm, trigger taxonomy, and alt text

**Existing L80–118**

- `ResolveInvocationKindAsync`
- Manual/Heartbeat eligibility filtering
- exactly one trigger
- overrides accepted only when trigger-eligible
- candidate “safety boundary” includes trigger rejection.

**Replacement ordered algorithm**

1. Resolve the project default for the outer fallback.
2. Load every valid available workflow; order configured default first, then ID.
3. Honor a resolvable explicit request override; otherwise consult the backlog-task override. An unavailable explicit ID is logged and selection continues.
4. Honor a resolvable conversational `use <workflow-id>` from revise feedback against the full available set.
5. With zero or one candidate, avoid the model and use the sole candidate or resolved default.
6. Otherwise ask the process-fit selector. Persist and emit the selection rationale.
7. Validate the selected workflow against the resulting decomposition. An explicit code-producing workflow without Build & Test is honored with a warning; an automatic selection without Build & Test is reselected or replaced by a suitable platform fallback.

Replace trigger filtering/taxonomy with:

> Workflow selection is trigger-agnostic. `Schedule` and `Event` describe automation admission, not eligibility for an interactive or heartbeat invocation. A workflow can declare multiple `triggers`; `trigger` remains the first-trigger compatibility alias. Automation creates backlog work that the coordinator subsequently picks up.

Replace selector fallback paragraph:

> The model has one initial attempt and one retry for an unusable selection. Supported parsing includes structured output and bounded candidate matching; ambiguous or unusable replies fall back deterministically. The selector prefers an available `default`/`standard` workflow, otherwise the first non-`code-review` candidate, otherwise the first entry. A thrown outer selection failure falls back to the orchestrator’s resolved project default.

**Replacement image alt L130**

> “Workflow selection: valid available workflows, explicit and conversational overrides, process-fit selection, bounded fallback, and post-decomposition compatibility checks.”

**Evidence/tests**

- Overrides/candidates: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:287-397`.
- Compatibility behavior: same file `:414-465`.
- Two attempts and actual fallback: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:82-178`, `:198-214`.
- Trigger types/arrays: `sabbour/agentweaver:apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:139-158`; `sabbour/agentweaver:apps/Agentweaver.Api/Workflows/WorkflowDtos.cs:18-21`.
- Tests: `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/WorkflowSelectorTests.cs:129-184`, `:245-259`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorOrchestratorTests.cs:147-245`.

## C. Correct child context wire format

**Existing L193**

> “…injected as the `## Boundaries and Decisions` block.”

**Replacement**

> Child workers receive their charter plus approved, active architectural/scope decisions compiled by `MemoryContextCompiler.CompileDecisionsAsync`. The compiler emits an `agentweaver.untrusted-context.v1` JSON envelope under `## Untrusted Project Context Data`; stored content remains untrusted data, not prompt headings or instructions. This decisions-only path excludes the full core-memory/learnings/session stack.

Evidence/tests: `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:156-172`, `:175-215`; `sabbour/agentweaver:tests/Agentweaver.Tests/Memory/MemoryContextCompilerSecurityTests.cs:22-69`, `:106-124`.

Retain advisory `isolation` wording; do not imply that `shared` authorizes a shared writable execution environment.

## D. State defaults without conflating launch and pickup

After L256 add:

> Omitted options on a direct/API/MCP launch default to `false`. Project heartbeat-pickup defaults are separate: `pickup_autopilot=true`, `pickup_auto_approve_tools=true`, and `max_ready_per_heartbeat=3`. Each successful claim snapshots the current persisted project values. The heartbeat service itself defaults enabled with a 10-second interval; these are not the ~20-second approval/provisioning wait heartbeats.

Evidence/tests: `sabbour/agentweaver:packages/Agentweaver.Domain/Project.cs:28-40`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:57-64`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/Ef/EfBacklogTaskStore.cs:407-425`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorOutcomeSpecTests.cs:615-625`.

**Anchor fix L311**

```text
./web.md#coordinator-orchestration-and-topology-view
→ ./web.md#coordinator-orchestration-and-unified-graph-view
```

The existing target heading is at `sabbour/agentweaver:docs/reference/web.md:131-133`.

At L306 replace “mermaid flow” with **“shared workflow-selection diagram”**; keep the deep-dive link.

---

# 3. `docs/reference/events.md`

## A. Correct child pipeline and descriptor claims

**Existing**

- L286: child nodes are `agent`, `rai`, `assemble-ready`.
- L70/L319–323: coordinator graph is “shape-only”; runtime status “NOT baked in.”
- L328–329: leaf edges always reach RAI and exactly two loopbacks.

**Replacement**

> The trimmed coordinator-child descriptor contains `agent` and `assemble-ready`; child runs do not execute their own RAI/review/merge/scribe pipeline. Collective review occurs over assembled child output.
>
> Coordinator descriptors include optional persisted status, reason, and terminal-stage fields, and selected-workflow assembly nodes become live as execution reaches them. Leaf edges enter the first resolved gate; each resolved gate has a coordinator loopback. Keep `coordinator.topology` as the separate subtask-state projection.

Use the graph-contract prose from API section 1C. Keep JSON searchable; do not replace it with a fixed pipeline bitmap.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:789-817`; graph evidence/tests in 1C.

## B. Correct confirmation and connection-closure semantics

**Existing L303**

> “In Phase 1 the coordinator run terminates after confirmation…”

Replace with:

> The persisted outcome spec becomes `confirmed`; `confirmedBy` identifies the accountable confirmer, including unattended confirmation through the normal seam. Confirmation advances into orchestration rather than inherently emitting `run.completed`. Direct mode skips this drafted-spec gate.

**Existing L299**

> “When autopilot is off… the SSE stream closes with a `done` frame after this event.”

Replace the categorical promise with:

> Outcome-definition and human-review waits are nonterminal. A connection may close at a gate; clients should use run/spec state and reconnect from the last per-run sequence rather than treating `done` as run completion. Do not require a `done` frame immediately after `coordinator.outcome_spec`: the local SSE closure condition checks `review.requested`, while durable subscribers use their terminal-event set.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:1162-1194`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:528-556`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:39-49`.

## C. Remove contradictory blanket terminal interpretation

**Existing L378**

> “…every terminal assembly path… `assembly_blocked: <reason>` (Failed)…”

**Replacement**

> Work-plan status and run terminal status are distinct. `assembly_blocked` can park a retryable assembly or ineligible subtask set while the run remains recoverable; it does not by itself terminate SSE. Actual terminal outcomes emit the corresponding terminal events. Durable replay drains its loaded batch, including diagnostics, before closing. Budget exhaustion escalates to `in_review` at the human-review stage rather than becoming terminal `steering_budget_exhausted`.

Retain L380–384’s existing durable-drain explanation; consolidate instead of leaving two conflicting accounts.

Evidence/tests: `sabbour/agentweaver:tests/Agentweaver.Tests/EfRunEventStreamTests.cs:120-153`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs:383-431`.

Also state explicitly:

> The SSE envelope sequence is the per-run replay cursor. The `seq` inside `coordinator.topology` is the topology projection’s own snapshot/delta counter; it is not a substitute for `Last-Event-ID`.

Existing topology definition: `sabbour/agentweaver:docs/reference/events.md:309-317`; SSE cursor implementation: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:485-493`.

## D. Anchor corrections

- L149: `../guide/runs.md#terminal-failure-diagnostics` → **`../guide/runs.md#failed-run-diagnostics`**. Actual heading: `sabbour/agentweaver:docs/guide/runs.md:296-305`.
- L233: `../tool-approval-sse-contract.md#tool-approval-pending-heartbeat-issue-212` → generated heading slug **`#toolapproval_pending-heartbeat-issue-212`**. Heading: `sabbour/agentweaver:docs/tool-approval-sse-contract.md:80-84`. Confirm the rendered slug during docs validation; punctuation removal must not convert the underscore into a hyphen.

Keep public `run.failed` taxonomy normalized; keep preview outcomes separate from Build/Test verdicts.

---

# 4. `docs/reference/mcp.md`

## A. Fix catalog completeness claim

**Existing L9**

> “This page documents each tool’s full parameters and return shape.”

**Replacement**

> This page documents selected tool parameters and workflows. The generated [MCP tool index](./mcp-tools.md) is authoritative for the complete tool-name and description catalog, including skills, marketplaces, and skill defaults. Use the exposed tool schema for the current complete parameter contract.

## B. Correct Coordinator introduction and Autopilot descriptions

**Existing L484**

> “The Coordinator agent drafts… then suspends…”

Use the launch-mode table from section 2A, preserving the already-correct defaults:

- `run_task`: `direct`.
- `coordinator_start`: `defineOutcome`.
- `run_submit`: legacy direct-mode Coordinator alias—not the removed standalone REST route.

Update the `autopilot` parameter descriptions at L362/L498:

> Auto-answer clarifying questions; when set at launch in `defineOutcome`, also confirm the drafted outcome unattended through the normal seam. Does not grant tool permissions. Defaults to `false`.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Mcp/Tools/RunTools.cs:129-154`; `sabbour/agentweaver:apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-44`; launch tests in 2A.

## C. Fix safe-tool approval

**Existing L502–505**

> “…covers `web_fetch` only. It does not bypass preview…”

**Replacement**

> Safe-tool auto-approval covers `web_fetch` and `start_preview`. For preview, it bypasses the human wait only: port, process-liveness, run/sandbox ownership, and publication validation still apply. Auto-grants emit `tool.auto_approved`; arbitrary shell, destructive, privileged, secret-bearing, and unrelated network permissions are not granted by this flag. The immutable launch policy is retained for retries and inherited by children. Heartbeat defaults apply only to heartbeat-created runs.

Evidence/tests: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs:151-177`; `sabbour/agentweaver:tests/Agentweaver.Tests/Sandbox/AgentPreviewGateTests.cs:71-121`.

## D. Backlog and trigger contracts

**Existing L1109**

> “Tasks progress through Backlog → Ready → Active, with terminal states of Done, Failed, and Archived.”

**Replacement**

> Task state is `Backlog`, `Ready`, or `Claimed`. Once claimed, the card is projected from its associated run into the canonical run buckets: Problems, Human Review, Active, or Done. Archiving hides eligible items; it is not another value in the task-state enum.

Evidence: `sabbour/agentweaver:packages/Agentweaver.Domain/BacklogTaskState.cs:3-8`; `sabbour/agentweaver:apps/Agentweaver.Api/Runs/WorkflowStageProjector.cs:13-43`.

At L1350, L1369, L1382, L1423 replace singular-only response descriptions with:

> Responses include `triggers` in declaration order plus legacy `trigger`, the first-trigger alias. MCP trigger writes still use full-workflow YAML generation/save; there is no dedicated structured trigger-edit MCP tool.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Workflows/WorkflowDtos.cs:18-21`, `:157-159`, `:267-268`.

Add pickup defaults to L1241–1254 using section 2D, including unattended outcome confirmation when pickup Autopilot is enabled.

## E. Provider-context consumer

At L126 add:

```markdown
See [AI execution context](./api.md#ai-execution-context) for the shared operation-bound preparation and acceptance contract.
```

Keep `PostAiAsync` behavior precise:

> The MCP server prepares context internally, rejects unresolved providers, and forwards `execution_key` as `If-Model-Provider-Key`; callers do not supply or receive that internal forwarding key as a tool parameter.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Mcp/AgentweaverApiClient.cs:395-455`.

**Do not add a provider-context image yet.**

---

# 5. `docs/reference/mcp-tools.md` — generated, no hand edits

Two upstream corrections are required.

### Source description

Existing generated L72 and source L14:

> “…destructive, privileged, preview, secret, and other network approvals remain gated.”

Replace the **source tool description**, not the generated Markdown:

> Start a Coordinator orchestration for a project from a plain-language goal. Optional launch policy can auto-approve the repository-defined safe tools `web_fetch` and `start_preview` and enable Autopilot. Preview validation still applies; arbitrary shell, destructive, privileged, secret-bearing, and unrelated network approvals are not granted by this policy.

Source: `sabbour/agentweaver:apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-22`. Behavior/test: `sabbour/agentweaver:tests/Agentweaver.Tests/Sandbox/AgentPreviewGateTests.cs:71-121`.

### Generated introduction

Existing L10 promises “the full parameter reference of each tool” in `mcp.md`. Correct the template to “selected tool parameters and workflows,” matching section 4A.

Generator location: `sabbour/agentweaver:scripts/gen-docs.mjs:142-163`.

Then run `node scripts/gen-docs.mjs` in the later authorized implementation pass. **This research pass did not run it.** Review other generated consumers the generator updates; do not manually patch just this output.

The generated `start_preview` description at L146 also says it returns the URL “once approved.” If adjusted upstream, say **“once approved by the applicable human or auto-approval policy”**, rather than implying every call displays a card.

---

# 6. `docs/reference/project-generation-model-settings.md`

Retain the tables and existing deep-dive link. Add this exact ordered expression after the settings table:

```text
project override → per-flow Generation setting → Generation.Model → gpt-5.6-sol
```

Add:

> These settings select models for generation flows, not runtime run-model pinning. Identifier validation accepts a syntactic family prefix; acceptance does not prove that the effective provider offers or can execute the model.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Generation/GenerationModelOptions.cs:12-40`, `:65-75`.

Correct the **consumer attribution**, not just stale line numbers:

- Existing L57 says `CoordinatorRunService.ActivateAsync` “resolves it.”
- Replacement:

> `CoordinatorRunService` passes the project outcome-spec override into `CoordinatorDraftInput`; `CopilotCoordinatorSpecDrafter` applies that override or its resolved generation default when starting the draft turn.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:486-497`, `:610-615`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CopilotCoordinatorSpecDrafter.cs:87-88`, `:143-145`, `:171-172`.

Owner remains the correct role for provider-settings updates: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:788-817`.

No model allowlist bitmap and no new diagram are needed.

---

# 7. `docs/reference/repo-blueprint-suggestions.md`

## Replace the related-picker section L83–92

**Existing**

- `/api/github/accounts`
- `/api/github/repos?account=…`
- old `/user/repos`/organization browsing explanation.

**Replacement**

> The suggestion service accepts an `owner/repo` or supported GitHub URL as heuristic input; that input does not authorize cloning or project creation. The project picker browses repositories available through the caller’s Repo App capability, requests a short-lived repository selection code for the chosen browse result, and submits that code to project creation.

| Method | Route | Contract |
|---|---|---|
| GET | `/api/github/repository-selections` | Safe repository/installation browse metadata from the caller’s live Repo App capability. |
| POST | `/api/github/repository-selections` | Verify the selected `repository_full_name` and mint a caller-bound, expiring, single-use selection code. |
| POST | `/api/projects` | GitHub creation consumes `repository_selection_code`; the server resolves clone metadata and credentials. |

Evidence/tests: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/GitHubRepositorySelectionEndpoints.cs:12-57`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/GitHubRepositorySelectionBroker.cs:71-123`, `:214-247`; `sabbour/agentweaver:tests/Agentweaver.Tests/Auth/GitHubRepositorySelectionBrokerTests.cs:21-99`.

## Keep mapping tables, with one clarification

> Rules run in table order and use substring heuristics over repository name, description, topics, languages, and root names; no LLM performs this recommendation. `has_issues` contributes a display signal, not a keyword match in the mapping text.

The existing L49 wording says `has_issues` feeds both mapping and display; narrow it as above. Mapping text omits `has_issues`, although signal presence affects the final generic confidence.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Blueprints/GitHubRepoBlueprintSuggestionService.cs:126-180`.

Keep fallback, but qualify L43:

> HTTP failures, service timeouts, and unavailable repository metadata return the template fallback; cancellation requested by the caller is propagated.

Evidence: same file `:85-104`.

Keep the existing deep-dive/experience links; no suggest-response-to-clone diagram edge.

---

# 8. `docs/reference/resilient-assembly-review.md`

Most transition/scoping text is already current. Preserve it.

## Correct public failure claims

**Existing L52**

> `run.failed` … `reason=commit_failed_persistent`, `evidence=<exception summary + lock diagnostics>`.

**Replacement table row**

> `run.failed` (child): bounded public `{ message, errorCode, retryable }` failure contract. Persistent commit failure may originate from internal child-turn failure evidence, but raw exception/lock evidence is not the public event payload.

**Existing L151**

> “…`ChildTurnFailedOutput.Evidence` string and the `run.failed` event…”

**Replacement**

> The following fields describe internal `ChildTurnFailedOutput.Evidence`/lock diagnostics. Do not expect this raw evidence in public `run.failed`: persistence and replay normalize that event to the bounded public failure contract. Authorized diagnostic surfaces must be documented according to their own projection; they are not an unrestricted raw-evidence endpoint.

Evidence:

- Normalization reads allowlisted error code/retryability and derives message: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/StructuredRunFailureTerminal.cs:104-149`.
- Persistence normalization: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:68-70`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs:79-81`.
- REST/SSE normalization regression tests: `sabbour/agentweaver:tests/Agentweaver.Tests/Auth/ProjectRunAuthorizationTests.cs:77-113`, `:116-193`.

Keep the already-correct:

- Autonomous budget exhaustion → human review.
- Human request-changes resets the autonomous mandate without a round-trip cap.
- Implicated authors may rotate; dependent rebuilds do not automatically lock out their authors.
- Same-author fresh dispatch with accumulated context is supported; no-context fallback escalates.

Tests: `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs:383-431`, `:467-549`, `:949-1071`.

Keep repository contribution-review rejection rules distinct from this runtime protocol.

---

# 9. `docs/reference/token-usage.md`

**No factual replacement is required by the audited concepts.** Keep the endpoint/DTO/status tables and the two-line formula.

Verified distinctions:

- Project metrics merge durable model/agent fallback with telemetry.
- Run token breakdown returns telemetry when it has agent data, otherwise durable fallback.
- Project/run inspection requires Viewer.
- `totalNanoAiu` is an integer `long`; it is not a display-rounded currency amount.
- Dashboard compatibility fields are not the full token-metrics DTO.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:79-110`, `:121-157`; `sabbour/agentweaver:apps/Agentweaver.Api/Metrics/MetricsDtos.cs:98-129`; `sabbour/agentweaver:apps/Agentweaver.Api/Metrics/DashboardReadService.cs:593-622`.

No diagram consumer action.

---

# 10. `docs/reference/unified-steering.md`

## A. Replace single-author escalation claim L41

**Existing**

> “…single-agent squads with no eligible rotation author, the first rejection therefore routes straight to human review…”

**Replacement**

> A released pod can make an assembly-eligible child non-resumable, causing `dispatch_fresh` rather than `in_place_steer`. Lack of an alternate author does not itself require immediate escalation: with accumulated feedback/context, the coordinator can dispatch a fresh run to the same author while preserving prior work and leaving the lockout roster unchanged. Without context, it escalates to human review. Autonomous recovery budgets still bound repeated redispatch.

Evidence/tests: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2447-2583`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/UnifiedSteeringTests.cs:245-320`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs:949-1071`.

## B. Replace budget paragraph L71

**Existing**

> “Assembly may surface this as `assembly_blocked` with reason `steering_budget_exhausted`.”

**Replacement**

> When autonomous budgets are exhausted, the decider chooses `proceed` and assembly escalates durably to `in_review`, stage `review`, with the Coordinator run `awaiting_review`. It does not latch a terminal `assembly_blocked`. A human request-changes provides a new supervised mandate and unconditionally resets the relevant autonomous steering budgets; `HumanReviewRoundTrips` is telemetry, not a cap.

Evidence/tests: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:440-451`; `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs:383-431`, `:512-549`.

## C. Correct failure table L58 and paragraph L62

Do not expose `child_executor_failed:{executor}` as the public `run.failed.reason` contract.

Replace with:

> Persistent child executor failure terminalizes the child and marks its workflow step failed. Internal recovery retains executor/failure context and can issue a visible fresh-dispatch decision. The public `run.failed` event remains the normalized `{ message, errorCode, retryable }` contract.

Retain the effect-marker conditions: a directive is not applied merely because launch was attempted; confirmed child effects are required. Tests: `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/UnifiedSteeringTests.cs:338-366`, `:578-640`.

---

# 11. `docs/reference/web.md`

## A. Configuration and API-prefix correction

**Existing L11/L16–18**

> Runtime API base is `/api`; “the `/api` prefix comes from the base URL.”

**Replacement**

> `API_URL` is the API **origin**, without an `/api` suffix. Runtime `window.__AGENTWEAVER_CONFIG__.API_URL` takes precedence over `VITE_API_URL`; an explicit empty runtime string means same-origin and must not fall back to localhost. The API client adds one `/api` prefix to XHR paths; browser authentication redirects use origin-root `/auth/entra/*`.

Replace the `VITE_API_KEY` table entry with a note that the current browser client uses session authentication and does not consume that variable. **Do not silently copy the stale `.env.example` key into the documented runtime contract.**

Evidence/tests: `sabbour/agentweaver:apps/web/src/config.ts:1-40`; `sabbour/agentweaver:apps/web/src/api/client.ts:294-304`, `:1658-1662`; `sabbour/agentweaver:apps/web/src/__tests__/config.test.ts:64-81`, `:167-174`.

## B. Replace GitHub sign-in and legacy submission sections

**Existing L96–106**

`GitHubSignIn`, GitHub device flow, header sign-in, `HomePage` submission/watch screen.

**Replacement**

> Product sign-in uses Microsoft Entra ID. `SignInPage` and `AuthGate` establish/check the Agentweaver session; the API client sends session authentication. GitHub Repo App and Copilot App connections are purpose-specific capabilities, not alternative product identities. Start work through Coordinator orchestration or backlog pickup, not the retired standalone run form.

Evidence: `sabbour/agentweaver:apps/web/src/App.tsx:180-218`; `sabbour/agentweaver:apps/web/src/pages/SignInPage.tsx:90-107`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:290-327`.

## C. Refresh routes/source tree

Add the actually routed destinations:

- `/sessions`, `/settings`, `/assistant`.
- `/platform-settings` — platform-admin gated.
- `/projects/:projectId/skills`.
- `/projects/:projectId/cluster`.
- Project observability overview/traces/agents.
- `/projects/:projectId/team/:agentName/memory`.
- Note legacy project-session/global-observability redirects rather than presenting them as independent pages.

Source: `sabbour/agentweaver:apps/web/src/App.tsx:80-135`.

In L347–392’s ASCII tree remove retired `RunSubmitForm`/`GitHubSignIn` descriptions and “SettingsPage… not currently routed.” Keep a compact tree of current imports/routes: `StartOrchestrationDialog`, `OutcomeSpecPanel`, `WorkflowGraphPanel`, `AgentSessionPanel`, `SignInPage`, `SessionsPage`, `SettingsPage`, `SkillsPage`, `ClusterPage`, observability pages, `CoordinatorRunRoute`, and `AssistantRoute`.

## D. Fix project creation and starting work

**Existing L64**

> GitHub URL/local-path picker and personal-account-first repository list.

Replace with the Repo App browse → selection-code → creation contract in section 7.

**Existing L110**

> Dialog “collects a single Goal field.”

Replace with:

> The dialog accepts a goal, optional workflow selection, and launch automation options. “Direct” starts planning from the prompt; the outcome-definition action uses the default draft/confirm path. Both call the orchestration endpoint and navigate to the Coordinator run.

Evidence: `sabbour/agentweaver:apps/web/src/components/StartOrchestrationDialog.tsx:85-107`, `:235-250`.

**Delete L272’s New Run action and L326–334’s standalone New Run walkthrough.**

Replace with a short cross-link to **Start an orchestration**. `/api/projects/{id}/runs` returns `410`; do not present it as an agent-specific submission path. Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs:1093-1107`.

## E. Correct live graph rendering

**Existing L149**

> fixed assembly pipeline, “always… muted/dashed,” never running.

Use section 1C’s dynamic/live assembly explanation.

**Existing L184**

> two `getBezierPath` segments, shared junction, “no hard arrowheads.”

**Replacement**

> Forward edges use `SpineEdge`, a stepped orthogonal connector built by `buildSteppedConnectorRoute` and `buildBridgedOrthogonalPath`. It renders arrow markers, crossing bridges, and computed connector junctions. It is not a two-Bezier-segment router.

Evidence: `sabbour/agentweaver:apps/web/src/components/WorkflowGraphPanel.tsx:1356-1407`.

## F. Human-action wording

- Update the toggle explanation to distinguish clarifying-question Autopilot from safe-tool auto-approval; name `web_fetch` and `start_preview`.
- Do not say every `coordinator.assembly_review_requested` opens human buttons. Require the **human-review** gate; automated gate events share that family.
- Do not tell users that terminal failed/declined runs can always be amended in place. Distinguish recoverable blocked states from terminal runs and use server-reported steerability.
- Keep child question/approval actions targeted at the actual child run.
- Retain existing memory-list-versus-compilation/provenance distinctions.
- Keep preview failure visible independently of the Build/Test verdict.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:403-409`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs:151-177`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1946-1973`; memory tests in section 2C.

---

# Required shared/deep-dive consumer actions

Use these stable targets; **do not author competing reference-specific images**.

| Consumer location | Required action |
|---|---|
| `api.md`, SSE section ~L624 | Add `../diagrams/canonical-durable-event-stream.png`; remove memory-only/restart-loss prose. |
| `api.md`, Coordinator section ~L1489 | Add `../diagrams/canonical-coordinator-journey.png` after the shared owner reconciles Direct/defineOutcome/Autopilot. |
| `api.md`, preview section ~L1023 | Add `../diagrams/sandbox-browser-preview-fig1.png`; keep disabled-preview kubectl fallback in text. |
| `coordinator.md`, launch section ~L34 | Add `../diagrams/canonical-coordinator-journey.png` with the reconciled launch prose. |
| `coordinator.md`, existing L130 image | Retain `../diagrams/canonical-workflow-selection.png`; update stale alt/surrounding algorithm. Shared owner must reconcile the asset. |
| `coordinator.md`, assembly section ~L265 | Add `../diagrams/resilient-assembly-review-fig1.png`; retain state/endpoint tables. |
| `events.md`, replay/waits ~L227 | Add `../diagrams/canonical-durable-event-stream.png`. |
| `events.md`, Coordinator lifecycle ~L336/362 | Add `../diagrams/resilient-assembly-review-fig1.png`; retain event payload definitions. |
| `mcp.md`, provider section ~L126 | Add `./api.md#ai-execution-context`. **No image pending shared ownership assignment.** |
| `mcp.md`, run-control section ~L330 | Add `../diagrams/canonical-coordinator-journey.png`. |
| `resilient-assembly-review.md`, ~L14 | Add `../diagrams/resilient-assembly-review-fig1.png`; preserve transition tables. |
| `unified-steering.md`, signals ~L16 | Add `../diagrams/unified-steering-fig1.png`; preserve signal/decision tables. |
| `unified-steering.md`, budgets ~L64 | Add `../diagrams/resilient-assembly-review-fig1.png` alongside corrected escalation prose. |
| `web.md`, Coordinator journey ~L112 | Add `../diagrams/canonical-coordinator-journey.png`; remove standalone submission walkthrough. |

Retain existing deep-dive links for generation model settings, repository suggestions, resilient assembly, unified steering, Coordinator internals, workflow selection, and Assistant runtime. Update stale anchor/“mermaid” wording identified above; do not change shared asset ownership or provenance independently.

**Ownership constraint:** `reference-provider-context-lifecycle` appears in the audit but is absent from `plan-reference.json`’s assigned concept IDs and proposed diagram names. It remains a **shared planning placeholder**. Prose consolidation and the API anchor link are actionable; naming/authoring a canonical asset is not.

## Explicit unresolved item

I searched current implementation, tests, scoped docs, and the reference audit for the literal identifiers **`await_user`** and **`memory_compile`**; neither exists in this worktree. Therefore, I cannot ground a replacement claiming either is a current runtime stage. The verified child graph is `agent → assemble-ready`; outcome waiting uses the Coordinator confirmation RequestPort. Do not introduce a `memory_compile` or `await_user` node from inference. This is the only requested identifier distinction that remained unverified.
