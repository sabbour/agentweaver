# Data Model: Single-Agent File-Editing Run

## Entity: Run
- **Fields**
  - `id` (UUID, required)
  - `originatingBranch` (string, required)
  - `modelSource` (enum: `copilot_sdk | microsoft_foundry`, required)
  - `taskPrompt` (string, required, non-empty)
  - `submittedBy` (string, required; identity of the user who submitted the run — the named human accountable for the run per FR-024; derived from authentication context and preserved for the full retention window)
  - `status` (enum: `queued | running | completed | failed | bounded | awaiting_review | approved | declined | merged | merge_conflict`)
  - `createdAt`, `startedAt`, `completedAt` (timestamps)
  - `maxSteps`, `maxDurationSeconds` (integers, required)
  - `sessionId` (foreign key, required once created)
  - `diffSummary` (text/json, nullable until terminal)
  - `failureReason` (string, nullable)

- **Validation Rules**
  - `modelSource` must be one of the two supported providers.
  - Run cannot enter `merged` unless review decision is `approved`.
  - Terminal statuses: `completed`, `failed`, `bounded`, `merged`, `declined`, `merge_conflict`.

## Entity: Session
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key to Run, unique)
  - `artifactDir` (absolute path, required)
  - `worktreePath` (absolute path, required)
  - `originatingCommit` (sha, required)
  - `createdAt` (timestamp)

- **Validation Rules**
  - `artifactDir`/`worktreePath` must resolve under configured run-root.
  - One session per run.

## Entity: Event
The durable, append-only, per-run event log. (Formerly modeled as a generic "Step".)
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key, required)
  - `sequence` (int64, required, monotonic per run; the persisted resumable cursor)
  - `type` (enum, required, one of:
    - lifecycle: `run.started | run.completed | run.failed | run.bounded`
    - content: `agent.message | tool.call | tool.result | tool.rejected | tool.error`
    - review/merge: `review.requested | review.approved | review.declined | merge.completed | merge.failed`)
  - `timestamp` (timestamp, required, informational only - not used for ordering)
  - `callId` (UUID, nullable; required on tool events `tool.call | tool.result | tool.rejected | tool.error`)
  - `payload` (JSON, required)

- **Validation Rules**
  - `(runId, sequence)` must be unique and assigned monotonically per run.
  - Ordering within a run is by `sequence` only; `timestamp` MUST NOT be used to order.
  - Every `tool.result`, `tool.rejected`, and `tool.error` MUST carry the `callId` of a prior `tool.call` for the same run.
  - The log is append-only (events are immutable once written).

- **Retention**
  - Persisted for the full run plus a bounded post-completion retention window.
  - Replay from any `lastSeenSequence` within the window MUST survive process restarts.
  - Delivery is at-least-once; consumers deduplicate by `(runId, sequence)`.

## Entity: ToolOperation
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key, required)
  - `callId` (UUID, required; correlates the `tool.call` event with its `tool.result | tool.rejected | tool.error` event)
  - `toolName` (enum: `read_file | write_file`)
  - `requestedPath` (string, required)
  - `resolvedPath` (string, nullable when rejected)
  - `result` (enum: `success | rejected | error`; maps to the `tool.result | tool.rejected | tool.error` event respectively)
  - `errorCode` (enum: `PATH_ESCAPE | NOT_FOUND | PERMISSION | UNKNOWN`, nullable)
  - `createdAt` (timestamp)

- **Validation Rules**
  - `requestedPath` cannot be absolute.
  - `resolvedPath` must remain inside artifact dir for `success`.
  - `callId` must match the originating `tool.call` event's `callId`.

## Entity: ReviewDecision
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key, unique)
  - `decision` (enum: `approve | decline`)
  - `reviewer` (string/user id, required)
  - `comment` (string, optional)
  - `createdAt` (timestamp)
  - `mergeResult` (enum: `not_attempted | merged | conflict | failed`)

- **Validation Rules**
  - Exactly one final decision per run for this slice.
  - Merge attempt allowed only for `approve`.
  - Each decision and merge outcome is also emitted to the Event log: `review.requested` when review is solicited, `review.approved`/`review.declined` for the decision, and `merge.completed`/`merge.failed` for the merge outcome.

## Entity: OperationalRecord
The per-run operational store for debugging, compliance review, and capacity analysis.
Distinct from the per-run Event log; not exposed to end users.
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key, unique)
  - `submittedBy` (string, required; echoed from Run for compliance queries without joining the full event log)
  - `modelSource` (enum: `copilot_sdk | microsoft_foundry`, required)
  - `startedAt`, `endedAt` (timestamps)
  - `stepCount` (integer, required; total number of agent loop steps taken)
  - `outcome` (enum: `completed | failed | bounded | merged | declined | merge_conflict`)
  - `policyTrace` (JSON array, required; ordered list of governance policy decisions reached during the run — each entry records the policy name, decision outcome, and timestamp, enabling a compliance reviewer to reconstruct all policy outcomes per SC-010)
  - `createdAt` (timestamp)

- **Validation Rules**
  - One OperationalRecord per run.
  - `policyTrace` must include an entry for every tool-permission check, model-source validation, sandbox boundary enforcement, run-limit enforcement, and human-approval gate evaluation (SC-010).
  - `submittedBy` must match `Run.submittedBy`.

- **Retention**
  - Retained for at least as long as the associated per-run Event log (FR-028).
  - Not subject to at-least-once replay; intended for operational queries, not streaming.

## Relationships
- Run `1:1` Session
- Run `1:N` Event (the per-run append-only event log)
- Run `1:N` ToolOperation
- Run `1:1` ReviewDecision
- Run `1:1` OperationalRecord

## State Transitions
- `queued -> running`
- `running -> completed | failed | bounded`
- `completed -> awaiting_review`
- `awaiting_review -> approved | declined`
- `approved -> merged | merge_conflict | failed`
- `declined` is terminal

Each transition above emits a corresponding lifecycle or review/merge event on the per-run Event log (e.g. `run.started`, `run.completed`, `run.failed`, `run.bounded`, `review.requested`, `review.approved`, `review.declined`, `merge.completed`, `merge.failed`). The Event log spans the full run lifecycle through merge.
