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
| `agent.turn.end` | When the model finishes a turn | `turnId` |
| `tool.call` | Before the runtime evaluates a tool invocation against the sandbox policy | `callId`, `toolName`, `arguments` |
| `tool.result` | After an approved tool runs successfully | `callId`, `content` |
| `tool.error` | After a tool is denied by the sandbox policy, or fails for any other reason such as a missing file or I/O failure | `callId`, `errorMessage` |
| `run.completed` | When the agent turn produces no file changes and the run terminates without a review gate | `result` |
| `run.failed` | When the runtime, provider, or content-safety flow ends the run in failure | `reason` |
| `run.bounded` | When the run hits a step-count or wall-clock bound | `limit_type`, `step_count` |
| `review.requested` | After the worktree is committed and the review tree hash is stored | `tree_hash`, `request_id` |
| `review.approved` | When the owner approves the run and the merge proceeds | *(none)* |
| `review.declined` | When the owner declines the run | *(none)* |
| `merge.completed` | After an approved run merges cleanly into the originating branch | `merged_commit_hash`; `previous_head_sha` (direct path only) |
| `merge.failed` | After an approved run cannot merge back cleanly | `reason` |

## Tool event pairing

Each `tool.call` carries a `callId` in its payload. The matching `tool.result` or `tool.error` repeats that same `callId`, so clients can pair tool outcomes with calls without relying on adjacent events. A policy denial is reported as a `tool.error`, not a separate event type.

## Provider parity

Both the GitHub Copilot and Microsoft Foundry runners stream text as `agent.message.delta` events. Each delta carries a `delta` chunk and the `messageId` it belongs to. Both providers surface the same tool event vocabulary. For each tool the agent runs, the stream carries a `tool.call`, followed by a `tool.result` for an approved tool (with its real content) or a `tool.error` for a denial or failure. The Copilot provider reads these from the tool-execution lifecycle that flows inline through the streaming response, so an observer sees individual tool activity on Copilot runs at parity with Foundry.

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

This event fires when the agent turn completes and the worktree contains no file changes. The `result` field is `"no_changes"`. The run terminates immediately on this path — no `review.requested` is emitted and no review gate is reached. When the agent does produce changes, `run.completed` is not emitted; the run transitions to `review.requested` instead.

### `run.failed`

This event marks a terminal failure. The `reason` field identifies the cause. When the agent's output is blocked by content safety policy, `reason` is `"content_safety"` and the run never reaches the review gate. Other values reflect infrastructure or watch-loop errors (for example, `"watch_loop_error"`).

### `run.bounded`

This event marks a run that exceeded enforced limits. `limit_type` is `step-count` or `wall-clock`.

### `review.requested`

This event anchors the review gate. `tree_hash` identifies the committed worktree state that the human reviews and that the merge step later verifies. `request_id` is an informational correlation id for the underlying workflow review request; it is not required for the review decision.

### `review.approved`

This event records that an approve decision was accepted. It carries no payload fields. It is followed immediately by either `merge.completed` or `merge.failed`. A blocked (retriable) approve does not emit this event — the run stays at the review gate and `review.requested` remains the last event on the stream.

### `review.declined`

This event records a decline decision. It carries no payload fields. The originating branch remains unchanged.

### `merge.completed`

This event records a successful merge. `merged_commit_hash` is always present. In the primary workflow path, the field contains the full result string in the format `merged:{commitHash}` (e.g., `"merged:34c09ee..."`). In the direct fallback path (post-restart recovery with no checkpoint), the field contains just the commit hash, and `previous_head_sha` is also present — the SHA the originating branch pointed to before the merge, useful for auditing and rollback.

### `merge.failed`

This event records why an approved run could not merge. Terminal reasons are branch divergence with unresolvable conflicts, and a tree-hash mismatch (the worktree branch changed after the run was reviewed). A checked-out originating branch is not a terminal reason — if the branch is checked out but the working tree is dirty, the approve is blocked retriably and no event is emitted until the condition is resolved.
