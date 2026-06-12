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
| `review.requested` | After the worktree is committed and the review tree hash is stored | `tree_hash`, `request_id` |
| `review.approved` | When the owner approves the run and the merge proceeds | *(none)* |
| `review.declined` | When the owner declines the run | *(none)* |
| `review.changes_requested` | When the human reviewer requests a revision | `comment` |
| `revision.started` | When a request-changes revision cycle begins | `revision`, `message` |
| `merge.started` | When an approved merge begins execution | `tree_hash` |
| `merge.completed` | After an approved run merges cleanly into the originating branch | `merged_commit_hash`; `previous_head_sha` (direct path only) |
| `merge.failed` | After an approved run cannot merge back cleanly | `reason` |

## Tool event pairing

Each `tool.call` carries a `callId` in its payload. The matching `tool.result` or `tool.error` repeats that same `callId`, so clients can pair tool outcomes with calls without relying on adjacent events. A policy denial is reported as a `tool.error`, not a separate event type.

## Provider parity

Both the GitHub Copilot and Microsoft Foundry runners stream text as `agent.message.delta` events. Each delta carries a `delta` chunk and the `messageId` it belongs to. Both providers emit `agent.turn.end` to close the final turn, giving the frontend a consistent signal to close the turn bubble regardless of which provider is active.

Both providers surface the same tool event vocabulary. For each tool the agent runs, the stream carries a `tool.call`, followed by a `tool.result` for an approved tool (with its real content) or a `tool.error` for a denial or failure. The Copilot provider reads these from the tool-execution lifecycle that flows inline through the streaming response, so an observer sees individual tool activity on Copilot runs at parity with Foundry.

SDK-internal tools (`report_intent`, `report_outcome`, `glob`, `agent.tools`) are suppressed from the event stream entirely. These are housekeeping operations that never pass through the sandbox permission handler and would confuse the frontend if rendered as tool cards.

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

## Model-assisted casting

Creating a casting proposal in `free_text` or `analysis` mode starts a MAF run on GitHub Copilot. That run emits events under the same event model as a regular scaffolder run — the same envelope, the same event types, and the same SSE endpoint.

The `run_id` returned in the `POST /api/projects/{id}/casting/proposals` response identifies that run. Stream its events from `GET /api/runs/{run_id}/stream` to observe the model's reasoning as it builds the proposal.

The casting wizard in the web UI streams these events during the review step using the same timeline rendering as the watch screen. The CLI streams them inline while waiting for the proposal to become ready.

No `.squad/` files are written during this run. The run produces a proposal, not a commit. Files are only written after the user confirms the proposal through `POST /api/projects/{id}/casting/proposals/{pid}/confirm`.

Scenario-mode proposals resolve without a model run. The `run_id` field in their proposal response is `null`.
