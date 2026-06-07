# Data Model: Single-Agent File-Editing Run

## Entity: Run
- **Fields**
  - `id` (UUID, required)
  - `originatingBranch` (string, required)
  - `modelSource` (enum: `copilot_sdk | microsoft_foundry`, required)
  - `taskPrompt` (string, required, non-empty)
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

## Entity: Step
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key, required)
  - `sequence` (int64, required, monotonic per run)
  - `type` (enum: `agent_message | tool_call | tool_result | lifecycle`)
  - `timestamp` (timestamp, required)
  - `payload` (JSON, required)

- **Validation Rules**
  - `(runId, sequence)` must be unique.
  - Every `tool_result` must reference a prior `tool_call`.

## Entity: ToolOperation
- **Fields**
  - `id` (UUID, required)
  - `runId` (foreign key, required)
  - `toolName` (enum: `read_file | write_file`)
  - `requestedPath` (string, required)
  - `resolvedPath` (string, nullable when rejected)
  - `result` (enum: `success | rejected | error`)
  - `errorCode` (enum: `PATH_ESCAPE | NOT_FOUND | PERMISSION | UNKNOWN`, nullable)
  - `createdAt` (timestamp)

- **Validation Rules**
  - `requestedPath` cannot be absolute.
  - `resolvedPath` must remain inside artifact dir for `success`.

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

## Relationships
- Run `1:1` Session
- Run `1:N` Step
- Run `1:N` ToolOperation
- Run `1:1` ReviewDecision

## State Transitions
- `queued -> running`
- `running -> completed | failed | bounded`
- `completed -> awaiting_review`
- `awaiting_review -> approved | declined`
- `approved -> merged | merge_conflict | failed`
- `declined` is terminal
