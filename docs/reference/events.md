# Events reference

Every run event uses the same envelope:

| Field | Type | Notes |
| --- | --- | --- |
| `runId` | string | Run identifier |
| `sequence` | integer | Per-run monotonic ordering key |
| `type` | string | Event type from the fixed taxonomy |
| `timestamp` | ISO 8601 string | Informational only |
| `payload` | object | Type-specific data |
| `callId` | string | Present on tool events only |

Clients should order and deduplicate events by `sequence`.

## Event taxonomy

| Type | When it fires | Payload fields |
| --- | --- | --- |
| `run.started` | After the run record and worktree are created, before the agent loop starts | `submitting_user`, `model_source`, `repository_path`, `originating_branch` |
| `agent.message` | When the model returns visible text that passes content safety | `text` |
| `tool.call` | Immediately before the runtime dispatches `read_file` or `write_file` | `path`, `operation` |
| `tool.result` | After a tool succeeds | `path`, `bytes_read_or_written` |
| `tool.rejected` | After a sandbox rule rejects a tool request | `path`, `reason` |
| `tool.error` | After a tool fails for a non-policy reason, such as not found or I/O failure | `path`, `error_message` |
| `run.completed` | When the model returns without any more tool calls | `step_count` |
| `run.failed` | When the runtime, provider, or content-safety flow ends the run in failure | `reason` |
| `run.bounded` | When the run hits a step-count or wall-clock bound | `limit_type`, `step_count` |
| `review.requested` | After the worktree is committed and the review tree hash is stored | `tree_hash` |
| `review.approved` | When the owner approves a completed run | `tree_hash`, `approved_by` |
| `review.declined` | When the owner declines a completed run | `declined_by` |
| `merge.completed` | After an approved run merges cleanly into the originating branch | `merged_commit_hash` |
| `merge.failed` | After an approved run cannot merge back cleanly | `reason` |

## Tool event pairing

Each `tool.call` gets a `callId` in the event envelope. The matching `tool.result`, `tool.rejected`, or `tool.error` repeats that same `callId`, so clients can pair tool outcomes with calls without relying on adjacent events.

## Event details

### `run.started`

This is the first event in a healthy run. It records the accountable user, selected provider, repository path, and originating branch.

### `agent.message`

This event carries model text that the runtime allows to reach clients. If content safety fails, the message is withheld and the run ends through `run.failed` instead.

### `tool.call`

This event fires before the runtime touches the file system. `operation` is `read` or `write`, and `path` is the relative path requested by the model.

### `tool.result`

This event records a successful tool execution. `bytes_read_or_written` reports the size of the read content or written content in UTF-8 bytes.

### `tool.rejected`

This event records a sandbox policy rejection, such as an absolute path, `..` traversal, or a symlink escape. The runtime uses `reason` to explain which rule blocked the request.

### `tool.error`

This event records execution failures that are not policy rejections. Common cases are missing files, access denied, or other I/O problems.

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
