# Events reference

Every run event uses the same envelope:

| Field | Type | Notes |
| --- | --- | --- |
| `runId` | string | Run identifier |
| `sequence` | integer | Per-run monotonic ordering key |
| `type` | string | Event type from the fixed taxonomy |
| `timestamp` | ISO 8601 string | Informational only |
| `payload` | object | Type-specific data |
| `callId` | string | A field of the `payload` on tool events (`tool.call`, `tool.result`, `tool.error`); pairs a tool outcome with its call |

Clients should order and deduplicate events by `sequence`.

## Event taxonomy

| Type | When it fires | Payload fields |
| --- | --- | --- |
| `agent.turn.start` | When the model begins a turn | `turnId` |
| `agent.message.delta` | When the model streams a chunk of visible text — emitted by both the GitHub Copilot and Microsoft Foundry runners | `delta`, `messageId` |
| `agent.message` | Fallback: emitted only when a turn produced no token deltas (Foundry runner only; tool-call-only turns or empty streams) | `content` |
| `agent.turn.end` | When the model finishes a turn (emitted by both runners; closes the turn bubble in the frontend) | `turnId` |
| `agent.intent` | When the agent calls `report_intent` before a major step | `intent` |
| `agent.system_prompt` | At run start, after the system prompt is set | `provider`, `prompt` (full text), `note` (optional) |
| `agent.tools` | At run start, listing the tools registered for this run | `tools` (string array of tool names) |
| `tool.call` | Before the runtime evaluates a tool invocation against the sandbox policy | `callId`, `toolName`, `arguments` |
| `tool.result` | After an approved tool runs successfully | `callId`, `content` |
| `tool.error` | After a tool is denied by the sandbox policy, or fails for any other reason such as a missing file or I/O failure | `callId`, `errorMessage` |
| `tool.approval_required` | When a tool call is paused awaiting human approval | `request_id`, `tool_name`, `url` (optional), `intention` (optional) |
| `run.completed` | When the watch loop determines the run is terminal with no file changes (watch-loop only; never emitted by the runner) | `result` |
| `run.outcome` | Agent self-assessment of task completion, emitted just before `run.completed` | `achieved` (bool), `reason` |
| `run.failed` | When the runtime, provider, or content-safety flow ends the run in failure | `reason` |
| `run.bounded` | When the run hits a step-count or wall-clock bound | `limit_type`, `step_count` |
| `run.cancelled` | When an in-progress run is cancelled because its project was deleted | *(none)* |
| `workflow.step` | When each workflow executor stage transitions | `step`, `status`, `label`, `timestamp_utc`, `agent_name` (agent step only), `reviewer` (review step only) |
| `run.workflow_graph` | Once at run start, carrying the full workflow graph descriptor for rendering the run topology | `GraphDescriptor` (see below) |
| `review.requested` | After the worktree is committed and the review tree hash is stored | `tree_hash`, `request_id` |
| `review.approved` | When the owner approves the run and the merge proceeds | *(none)* |
| `review.declined` | When the owner declines the run | *(none)* |
| `review.changes_requested` | When the human reviewer requests a revision | `comment` |
| `revision.started` | When a request-changes revision cycle begins | `revision`, `message` |
| `merge.started` | When an approved merge begins execution | `tree_hash` |
| `merge.completed` | After an approved run merges cleanly into the originating branch | `merged_commit_hash`; `previous_head_sha` (direct path only) |
| `merge.failed` | After an approved run cannot merge back cleanly | `reason` |
| `coordinator.started` | When a coordinator run begins drafting an OutcomeSpec from the user's goal | `goal` |
| `coordinator.outcome_spec` | When the coordinator has drafted an OutcomeSpec and suspended at the await-confirmation gate | `specId`, `status`, `desiredOutcome`, `scope`, `assumptions`, `clarifyingQuestions` |
| `coordinator.outcome_spec.confirmed` | When a human confirms the drafted OutcomeSpec and the coordinator run proceeds | `specId`, `confirmedBy` |
| `coordinator.work_plan` | When the coordinator has decomposed the confirmed spec into a persisted work plan | `workPlanId`, `status`, `subtasks`, `dependencies` |
| `coordinator.topology` | When the orchestration graph is first dispatched (snapshot) and on every subsequent subtask lifecycle transition (delta) | `version`, `kind`, `seq`, `nodes` (snapshot) / `changed` (delta), `edges` (snapshot) |
| `coordinator.graph` | When the unified coordinator graph shape changes (a subtask child run is dispatched, or the plan reaches its terminal snapshot) | a shape-only `GraphDescriptor` (variant `coordinator`) |
| `subtask.dispatched` / `subtask.running` / `subtask.assemble_ready` / `subtask.rai_flagged` / `subtask.completed` / `subtask.failed` | As a subtask's child run advances through its lifecycle | `subtaskId`, `childRunId`, `assignedAgent`, `selectedModelId`, `status` |
| `coordinator.steering` | When a steering directive is created or changes state | `directiveId`, `kind`, `targetChildRunId`, `status`, `instruction` |
| `coordinator.children_complete` | When every child subtask has reached a terminal status and the work plan moves to `awaiting_assembly` | `workPlanId` |
| `coordinator.assembly_started` | When the collective-assembly pipeline claims the plan (`awaiting_assembly → assembling`, exactly-once) | `workPlanId`, `integrationBranch`, `subtaskCount` |
| `coordinator.assembly_blocked` | When assembly stops with NO partial work — an ineligible subtask, or a conflict building the integration branch | `workPlanId`, `reason`, and (conflict only) `conflictingBranch`, `conflictingFiles` |
| `coordinator.assembly_rai_started` / `coordinator.assembly_rai_completed` | The ONE collective RAI pass over the aggregate diff (advisory; never hard-blocks) | `workPlanId`, `integrationBranch` / `raiSafetyFlagged` |
| `coordinator.assembly_review_requested` | When the pipeline suspends at the ONE collective human-review gate | `workPlanId`, `integrationBranch`, `raiSafetyFlagged`, `hasChanges` |
| `coordinator.assembly_review_approved` | When the reviewer approves the combined output | `workPlanId` |
| `coordinator.assembly_changes_requested` | When the reviewer requests changes; the coordinator re-dispatches the inferred children | `workPlanId`, `redispatchSubtaskIds`, `inferredFiles`, `fellBackToAll`, `feedback` |
| `coordinator.assembly_merge_started` / `coordinator.assembly_merge_completed` / `coordinator.assembly_merge_failed` | The ONE collective merge of the integration branch into the originating branch | `workPlanId`, `integrationBranch` / `commitHash` / `reason`, `conflictingFiles` |
| `coordinator.assembly_scribe_started` / `coordinator.assembly_scribe_completed` | The ONE collective scribe pass after a successful merge (best-effort) | `workPlanId` |
| `coordinator.assembly_completed` | When collective assembly finishes and the work plan reaches `complete` | `workPlanId`, `integrationBranch`, `commitHash` |

## Tool event pairing

Each `tool.call` carries a `callId` in its payload. The matching `tool.result` or `tool.error` repeats that same `callId`, so clients can pair tool outcomes with calls without relying on adjacent events. A policy denial is reported as a `tool.error`, not a separate event type.

## Provider parity

Both the GitHub Copilot and Microsoft Foundry runners stream text as `agent.message.delta` events. Each delta carries a `delta` chunk and the `messageId` it belongs to. Both providers emit `agent.turn.end` to close the final turn, giving the frontend a consistent signal to close the turn bubble regardless of which provider is active.

Both providers surface the same tool event vocabulary. For each tool the agent runs, the stream carries a `tool.call`, followed by a `tool.result` for an approved tool (with its real content) or a `tool.error` for a denial or failure. The Copilot provider reads these from the tool-execution lifecycle that flows inline through the streaming response, so an observer sees individual tool activity on Copilot runs at parity with Foundry.

SDK-internal tools (`report_outcome`, `glob`) are suppressed from the event stream. `report_intent` is translated into an `agent.intent` event rather than suppressed — the raw tool call is hidden, but the intent text surfaces as a first-class event. `agent.tools` is a synthetic event emitted by the runtime, not an SDK tool.

## Event details

### `agent.message`

This event is emitted only by the Foundry runner, as a fallback when a turn produced no token deltas — for example, a tool-call-only turn or an empty stream. `content` carries the full text for that turn. It is never emitted alongside `agent.message.delta` events for the same turn.

During normal streaming, both the GitHub Copilot and Microsoft Foundry runners produce `agent.message.delta` events, each carrying a `delta` chunk and the `messageId` it belongs to.

### `tool.call`

This event fires before the runtime evaluates the request against the sandbox policy. `toolName` is the tool the model invoked, and `arguments` is the argument object it passed (for file tools, this includes the requested `path`).

### `tool.result`

This event records a successful tool execution. `content` carries the result the tool returned, such as the text of an approved file read.

### `tool.error`

This event records every tool outcome that is not a success. It covers sandbox policy denials — an absolute path, `..` traversal, or a symlink escape — as well as non-policy failures such as a missing file or an I/O error. `errorMessage` explains what went wrong. It never carries the contents of a file outside the sandbox, because a denied tool never runs.

### `run.completed`

This event is emitted exclusively by the watch loop (`RunWatchLoopService`) when the workflow reaches a terminal state with no file changes. The `result` field is `"no_changes"`. Neither the GitHub Copilot runner nor the Foundry runner emits this event; they emit `agent.turn.end` to close their final turn and let the watch loop determine terminal state. When the agent produces changes, `run.completed` is not emitted; the run transitions to `review.requested` instead.

### `run.failed`

This event marks a terminal failure. The `reason` field identifies the cause. When the agent's output is blocked by content safety policy, `reason` is `"content_safety"` and the run never reaches the review gate. Other values reflect infrastructure or watch-loop errors (for example, `"watch_loop_error"`).

### `run.bounded`

This event marks a run that exceeded enforced limits. `limit_type` is `step-count` or `wall-clock`.

### `run.cancelled`

This event marks a run that was cancelled because its project was deleted. The run transitions to a terminal state immediately and no further events are emitted. It carries no payload fields. The originating branch and any worktree state are cleaned up as part of the project deletion.

### `review.requested`

This event anchors the review gate. `tree_hash` identifies the committed worktree state that the human reviews and that the merge step later verifies. `request_id` is an informational correlation id for the underlying workflow review request; it is not required for the review decision.

### `review.approved`

This event records that an approve decision was accepted. It carries no payload fields. It is followed immediately by either `merge.completed` or `merge.failed`. A blocked (retriable) approve does not emit this event — the run stays at the review gate and `review.requested` remains the last event on the stream.

### `review.declined`

This event records a decline decision. It carries no payload fields. The originating branch remains unchanged.

### `merge.started`

This event fires immediately before the merge operation begins, bridging the gap between the approve action and the terminal `merge.completed` or `merge.failed` event. `tree_hash` identifies the committed worktree state being merged — the same value recorded by `review.requested`.

### `merge.completed`

This event records a successful merge. `merged_commit_hash` is always present. In the primary workflow path, the field contains the full result string in the format `merged:{commitHash}` (e.g., `"merged:34c09ee..."`). In the direct fallback path (post-restart recovery with no checkpoint), the field contains just the commit hash, and `previous_head_sha` is also present — the SHA the originating branch pointed to before the merge, useful for auditing and rollback.

### `merge.failed`

This event records why an approved run could not merge. Terminal reasons are branch divergence with unresolvable conflicts, and a tree-hash mismatch (the worktree branch changed after the run was reviewed). A checked-out originating branch is not a terminal reason — if the branch is checked out but the working tree is dirty, the approve is blocked retriably and no event is emitted until the condition is resolved.

### `agent.intent`

Emitted when the agent calls the `report_intent` internal tool before a major step. Not shown as a tool call card in the frontend — rendered as a lightweight lifecycle card with the intent text. The `intent` field is always a non-empty string.

### `agent.system_prompt`

Emitted once at run start when the system prompt is set. `provider` identifies which model provider is active. `prompt` carries the full system prompt text. In the frontend, this renders as a collapsible card showing a 120-character preview with character count; clicking expands the full text.

### `agent.tools`

Emitted once at run start listing the tool names registered for this run. Varies by sandbox policy (`run_command` is absent when shell execution is disabled). Rendered in the frontend as a row of `<Badge>` components.

### `revision.started`

Emitted when a request-changes revision cycle begins. `revision` is a 1-based counter. `message` is the reviewer's comment passed to the agent.

### `run.outcome`

The agent's self-assessment of task completion. `achieved: true` when the agent reports the task was completed; `false` when a critical step failed or was blocked (for example, a required tool call was denied by the sandbox). Emitted by the `report_outcome` internal tool, which is suppressed from ToolCallCard rendering. The frontend uses this to style `run.completed`: green for achieved, amber warning for not achieved. If the agent never calls `report_outcome` (older prompts or prompts that don't include the instruction), `run.completed` renders as success by default.

### `review.changes_requested`

Emitted when the human reviewer calls `POST /api/runs/{id}/request-changes`. `comment` is the reviewer's feedback passed to the agent for the revision cycle.

### `tool.approval_required`

Emitted when a tool call is paused waiting for human approval. `request_id` identifies the request and is used by `POST /api/runs/{id}/tool-approvals` and `POST /api/runs/{id}/tool-denials`. `tool_name` is the tool being called. `url` is the resource being accessed (for `web_fetch` and similar tools). `intention` is an optional human-readable description of what the agent intends to do. The run is paused until the human approves or denies; `tool.result` or `tool.error` follows once settled.

### `workflow.step`

Emitted by each workflow executor stage when it starts, completes, fails, or is skipped. The `step` field identifies the stage:

| Step | Executor |
|------|----------|
| `agent` | AI agent turn (task execution) |
| `rai` | RAI safety review |
| `review` | Human review gate |
| `merge` | Branch merge |
| `scribe` | Session logger |

Possible `status` values: `started`, `completed`, `failed`, `skipped`, `revise` (RAI step only).

The `label` field is a short human-readable description (e.g. `"Agent turn"`, `"RAI review"`). `timestamp_utc` is an ISO 8601 timestamp. The `agent` step includes `agent_name` (the team member running the turn). The `review` step includes `reviewer` (GitHub username) when a human review decision is recorded.

The web UI uses `workflow.step` events to drive the workflow diagram — each card in the Agent → Rai → Review → Merge → Scribe pipeline updates live as these events arrive.

### `run.workflow_graph`

Emitted once at run start, carrying a full snapshot of the run's workflow topology as a `GraphDescriptor`. It is built from the same code that wires the MAF workflow (no runtime reflection), so the rendered graph never drifts from the executors that actually run. The descriptor is persisted with the run's other events, so it is also available for terminal runs via replay and through `GET /api/runs/{id}/graph`. Child runs (`parent_run_id != null`) carry the `child` variant; all others carry the `full` variant.

`GraphDescriptor` shape (snake_case JSON):

```json
{
  "graph_id": "scaffolder-workflow-full",
  "variant": "full",
  "start_node_id": "agent",
  "nodes": [
    { "id": "agent", "label": "Agent", "role": "agent", "kind": "live", "node_type": "agent", "child_graph_ref": null }
  ],
  "edges": [
    { "from": "agent", "to": "rai", "cardinality": "direct", "loopback": false }
  ]
}
```

- `variant`: `"full"` | `"child"` | `"coordinator"`.
- `nodes[].id`: the logical node id (equals the `step` key in `workflow.step` events for live business nodes). `kind`: `"live"` | `"planned"`. `child_graph_ref`: optional reference to a nested graph.
- `nodes[].node_type`: self-declared category that drives the frontend's rendered shape — one of `"agent"` (an AI agent turn), `"action"` (a deterministic system op), `"gate"` (a human-in-the-loop decision/approval), `"terminal"` (a workflow endpoint/checkpoint), or `"subtask"` (a coordinator fan-out child reference). Required on every node. In the `full` variant: `agent`/`rai`/`scribe` are `agent`, `review` is `gate`, `merge` is `action`; in the `child` variant `assemble-ready` is `terminal`.
- `edges[].cardinality`: `"direct"` | `"fanout"` | `"fanin"`. `loopback`: `true` for revision-cycle back-edges (the target is an ancestor of the source).

Plumbing executors (input storers, adapters, terminals) are collapsed or hidden: hidden nodes are dropped and their edges are transitively re-stitched, and the scribe-path executors collapse into the single `scribe` node. The `full` variant nodes are `agent`, `rai`, `review`, `merge`, `scribe`; the `child` variant nodes are `agent`, `rai`, `assemble-ready`.

### `coordinator.started`

Emitted once when a coordinator run begins. A coordinator run is a Run with `ParentRunId == null` driven by the built-in Coordinator agent. `goal` carries the user's submitted goal text. The run reads the project's Feature 006 memories and decision-inbox entries as grounding context, then drafts an OutcomeSpec.

### `coordinator.outcome_spec`

Emitted when the coordinator has drafted an OutcomeSpec and suspended at the await-confirmation gate (`coordinator-confirmation-gate`). The OutcomeSpec is persisted to the memory store with status `awaiting_confirmation` before this event fires. `specId` is the persisted row id; `status` is `awaiting_confirmation`. `desiredOutcome`, `scope`, and `assumptions` are the drafted strings; `clarifyingQuestions` is an optional array. No decomposition or child dispatch occurs before a human confirms — the run blocks here until the confirm or revise seam is called.

### `coordinator.outcome_spec.confirmed`

Emitted when a human confirms the drafted OutcomeSpec through the confirm seam. The persisted OutcomeSpec advances to status `confirmed`. `specId` is the confirmed row id; `confirmedBy` is the confirming user. In Phase 1 the coordinator run terminates after confirmation (decomposition and child dispatch are Phase 2); a `run.completed` event follows.

### `coordinator.work_plan`

Emitted when the coordinator decomposes a confirmed OutcomeSpec into a work plan and persists it. `workPlanId` is the persisted plan id; `status` begins at `planned`. `subtasks` is the array of decomposed units of work, each carrying its `subtaskId`, `title`, `scope`, `assignedAgent`, `selectedModelId`, `phase`, `isolation`, and `status`. `dependencies` is the array of `{ subtaskId, dependsOnSubtaskId }` edges that constrain dispatch order. The work plan is the durable artifact behind the live `coordinator.topology` graph; subagents read from it once dispatched.

### `coordinator.topology`

Emitted to describe the orchestration graph as it executes. The payload is versioned (`version: 1`) and comes in two `kind`s, ordered by a per-coordinator `seq` counter:

- A `snapshot` (`kind: "snapshot"`, `seq: 0`) fires once when dispatch begins. It carries the complete `nodes` array and the `edges` array. There is one `coordinator` node (`id: "coordinator"`) plus one node per subtask (`id: "subtask-{id}"`). Each node carries `id`, `kind` (`coordinator` or `subtask`), `subtaskId`, `status`, `label`, `agent`, `model`, `childRunId`, `phase`, and `isolation`. Each edge is `{ from, to }`, meaning the `from` node (a dependency) must reach `assemble_ready`/`completed` before the `to` node (its dependent) is dispatched.
- A `delta` (`kind: "delta"`, `seq > 0`) fires on every subtask lifecycle transition. It carries a `changed` array of the node(s) whose state moved (replace by `id`); `edges` never change after the snapshot, so deltas omit them. A delta may carry the `coordinator` node when the work plan's own status transitions.

Clients render directly from these events and never compute topology themselves. Edge direction is always dependency to dependent.

### `coordinator.graph`

The unified coordinator view in the shared `GraphDescriptor` contract (the same shape returned by `GET /api/runs/{id}/graph` and emitted per-run as `run.workflow_graph`), so the frontend's generic renderer draws the coordinator, its fan-out subtask children, and the PLANNED Phase 3 collective-assembly stage with one code path. Emitted on the coordinator stream as a FULL, shape-only snapshot whenever the topology shape changes (a subtask child run is dispatched, or the plan reaches its terminal snapshot). It is built from the work plan (no reflection).

Unlike `coordinator.topology`, runtime status is NOT baked into the descriptor — it is shape only (consistent with `run.workflow_graph`); project status separately from the `subtask.*` / `coordinator.topology` streams. The payload is a `GraphDescriptor` with `variant: "coordinator"`, `graph_id: "coordinator:{coordinatorRunId}"`, `start_node_id: "coordinator"`:

- Node `coordinator` (`node_type: "agent"`, `role: "coordinator"`, `kind: "live"`).
- One `plan:subtask-{id}` node per subtask (`node_type: "subtask"`, `kind: "live"`) carrying optional `agent`, `model`, `phase`, `isolation`, `child_run_id` fields (omitted when null) and a `child_graph_ref` of `run:{childRunId}` once dispatched (null until then) so the child's own graph can be expanded via `GET /api/runs/{childRunId}/graph`.
- PLANNED collective-assembly chain (`kind: "planned"`): `planned:assembly-rai` (`agent`) → `planned:assembly-review` (`gate`) → `planned:assembly-merge` (`action`) → `planned:assembly-scribe` (`agent`).
- Edges: `coordinator` → each root subtask; dependency edges between subtasks; each terminal (leaf) subtask → `planned:assembly-rai`; then the assembly chain. The coordinator graph is a DAG (`loopback` always false). `coordinator.topology` remains emitted alongside for existing consumers; `coordinator.graph` is the unified-contract event.

### `subtask.*`

The `subtask.dispatched`, `subtask.running`, `subtask.assemble_ready`, `subtask.rai_flagged`, `subtask.completed`, and `subtask.failed` events track a single subtask's child run through its lifecycle on the coordinator stream. Each carries `subtaskId`, `childRunId`, `assignedAgent`, `selectedModelId`, and `status`. The subtask status advances `pending -> dispatched -> running -> {assemble_ready | rai_flagged | completed | failed}`. Each `subtask.*` event is paired with a `coordinator.topology` delta for the changed node, so observers can choose either the granular per-subtask signal or the graph view.

### `coordinator.steering`

Emitted when a steering directive is created through `POST /api/runs/{id}/steer` and as it changes state. `directiveId` identifies the directive; `kind` is `stop`, `redirect`, or `amend`; `targetChildRunId` is the targeted child run, or null for a broadcast to every active child; `instruction` is the direction relayed to the subagent(s); `status` advances `pending -> queued -> relayed -> applied`. A `stop` collapses to `applied` essentially immediately because it cancels the in-flight turn's token. A `redirect` or `amend` reaches `applied` only at the targeted subagent's next turn boundary, so observers should surface it as queued until then. Pause is not supported in Phase 2.

### `coordinator.children_complete` and `coordinator.assembly_*`

Phase 3 collective assembly runs ONE pipeline over the COMBINED output of all child runs, then flows back to the coordinator. Child output is git state, not in-memory text: each child commits to its own worktree branch. When every child subtask reaches a terminal status, the coordinator emits `coordinator.children_complete` and moves the work plan to `awaiting_assembly`.

A single background pipeline then drives the collective stages, each emitting a paired `coordinator.graph` so its planned assembly node flips to `kind: "live"`:

1. **Exactly-once claim** — a DB compare-and-swap transitions `awaiting_assembly → assembling`; only the winner proceeds. `coordinator.assembly_started` carries the `integrationBranch` name (`scaffolder/integration/{coordinatorRunId}`) and `subtaskCount`.
2. **Eligibility gate (no partial assembly)** — every subtask must be assembly-eligible (`assemble_ready`, or `completed` with no changes). If any is failed / rai_flagged / pending / blocked, or merging the eligible child branches into the integration branch conflicts, the pipeline emits `coordinator.assembly_blocked` (with `reason`, and `conflictingBranch`/`conflictingFiles` on a conflict) and STOPS — no RAI, no merge.
3. **Integration branch** — the eligible child branches are merged in dependency (topological) order off the coordinator's originating branch, producing one aggregate diff + tree hash.
4. **Collective RAI** (`coordinator.assembly_rai_started` → `coordinator.assembly_rai_completed`) — one RAI pass over the aggregate diff. It is advisory: it never hard-blocks, but `raiSafetyFlagged` is surfaced to the human reviewer.
5. **One human review gate** (`coordinator.assembly_review_requested`) — the pipeline suspends until a decision arrives via `POST /api/runs/{coordinatorRunId}/assembly/review`. Approve → `coordinator.assembly_review_approved`. Request changes → `coordinator.assembly_changes_requested` (the coordinator infers the affected children from the reviewer's `target_files` ∪ path tokens in `feedback`, intersected with each child's touched-files and expanded to dependents — `redispatchSubtaskIds`, `inferredFiles`, `fellBackToAll`), resets those subtasks to `pending`, returns the plan to `dispatching`, and re-dispatches. A pure decline is the terminal `assembly_declined` status.
6. **One merge** (`coordinator.assembly_merge_started` → `coordinator.assembly_merge_completed` with `commitHash`, or `coordinator.assembly_merge_failed` with `reason`/`conflictingFiles`).
7. **One scribe** (`coordinator.assembly_scribe_started` → `coordinator.assembly_scribe_completed`) — best-effort; a scribe failure does not fail the already-merged assembly.
8. **Completion** — `coordinator.assembly_completed` with the `integrationBranch` and `commitHash`; the work plan reaches `complete`.

Work-plan status flows `dispatching → awaiting_assembly → assembling → in_review → assembling` (during merge/scribe after approval) `→ complete`, plus the parked/terminal states `assembly_blocked`, `assembly_failed`, and `assembly_declined`. The per-child `subtask.rai_flagged` events and the collective `coordinator.assembly_rai_*` events are DISTINCT: the former is each agent stream's own RAI, the latter is the single RAI over the combined output. All events carry a monotonic `seq` on the coordinator stream.


## Model-assisted casting

Creating a casting proposal in `free_text` or `analysis` mode starts a MAF run on GitHub Copilot. That run emits events under the same event model as a regular scaffolder run — the same envelope, the same event types, and the same SSE endpoint.

The `run_id` returned in the `POST /api/projects/{id}/casting/proposals` response identifies that run. Stream its events from `GET /api/runs/{run_id}/stream` to observe the model's reasoning as it builds the proposal.

The casting wizard in the web UI streams these events during the review step using the same timeline rendering as the watch screen. MCP clients receive them as progress notifications during a `team_cast` tool call.

No `.squad/` files are written during this run. The run produces a proposal, not a commit. Files are only written after the user confirms the proposal through `POST /api/projects/{id}/casting/proposals/{pid}/confirm`.

Scenario-mode proposals resolve without a model run. The `run_id` field in their proposal response is `null`.
