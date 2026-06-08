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
| `run.started` | After the run record and worktree are created, before the agent loop starts | `submitting_user`, `model_source`, `repository_path`, `originating_branch` |
| `agent.turn.start` | When the model begins a turn | `turnId` |
| `agent.message.delta` | When the model streams a chunk of visible text | `delta`, `messageId` |
| `agent.message` | When the model returns a complete visible message that passes content safety | `content` |
| `agent.turn.end` | When the model finishes a turn | `turnId` |
| `tool.call` | Before the runtime evaluates a tool invocation against the sandbox policy | `callId`, `toolName`, `arguments` |
| `tool.result` | After an approved tool runs successfully | `callId`, `content` |
| `tool.error` | After a tool is denied by the sandbox policy, or fails for any other reason such as a missing file or I/O failure | `callId`, `errorMessage` |
| `run.completed` | When the model returns without any more tool calls | `summary` |
| `run.failed` | When the runtime, provider, or content-safety flow ends the run in failure | `errorMessage` |
| `run.bounded` | When the run hits a step-count or wall-clock bound | `limit_type`, `step_count` |
| `review.requested` | After the worktree is committed and the review tree hash is stored | `tree_hash` |
| `review.approved` | When the owner approves a completed run | `tree_hash`, `approved_by` |
| `review.declined` | When the owner declines a completed run | `declined_by` |
| `merge.completed` | After an approved run merges cleanly into the originating branch | `merged_commit_hash` |
| `merge.failed` | After an approved run cannot merge back cleanly | `reason` |

## Tool event pairing

Each `tool.call` carries a `callId` in its payload. The matching `tool.result` or `tool.error` repeats that same `callId`, so clients can pair tool outcomes with calls without relying on adjacent events. A policy denial is reported as a `tool.error`, not a separate event type.

## Provider parity

Both supported providers — GitHub Copilot and Microsoft Foundry — surface the same tool event vocabulary. For each tool the agent runs, the stream carries a `tool.call`, followed by a `tool.result` for an approved tool (with its real content) or a `tool.error` for a denial or failure. The Copilot provider reads these from the tool-execution lifecycle that flows inline through the streaming response, so an observer sees individual tool activity on Copilot runs at parity with Foundry.

## Event details

### `run.started`

This is the first event in a healthy run. It records the accountable user, selected provider, repository path, and originating branch.

### `agent.message`

This event carries the complete model message that the runtime allows to reach clients, in its `content` field. While a turn is streaming, the text arrives as a sequence of `agent.message.delta` events, each carrying a `delta` chunk and the `messageId` it belongs to. If content safety fails, the message is withheld and the run ends through `run.failed` instead.

### `tool.call`

This event fires before the runtime evaluates the request against the sandbox policy. `toolName` is the tool the model invoked, and `arguments` is the argument object it passed (for file tools, this includes the requested `path`).

### `tool.result`

This event records a successful tool execution. `content` carries the result the tool returned, such as the text of an approved file read.

### `tool.error`

This event records every tool outcome that is not a success. It covers sandbox policy denials — an absolute path, `..` traversal, or a symlink escape — as well as non-policy failures such as a missing file or an I/O error. `errorMessage` explains what went wrong. It never carries the contents of a file outside the sandbox, because a denied tool never runs.

### `run.completed`

This event means the model returned no further tool calls. The orchestrator still needs to commit the worktree and emit `review.requested` before the run reaches the review gate.

### `run.failed`

This event marks a terminal failure. Typical reasons include provider exceptions, unexpected runtime failures, explicit cancellation, or content-safety failures.

### `run.bounded`

This event marks a run that exceeded enforced limits. `limit_type` is `step-count` or `wall-clock`.

### `review.requested`

This event anchors the review gate. `tree_hash` identifies the committed worktree state that the human reviews and that the merge step later verifies.

### `review.approved`

This event records the approving user and the tree hash they approved. Merge starts only after this event is written.

### `review.declined`

This event records a decline decision. The originating branch remains unchanged.

### `merge.completed`

This event records the merge commit hash or fast-forward target after a successful merge back into the originating branch.

### `merge.failed`

This event records why an approved run could not merge. Typical reasons include branch divergence, conflicts, or a checked-out originating branch that cannot be advanced safely.
