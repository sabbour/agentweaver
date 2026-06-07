# Scaffolder API Reference

The Scaffolder backend is the single source of truth for run lifecycle,
streaming, review, and merge. Every client is a thin layer over these endpoints.

Base path: `/api`. All endpoints require a bearer API key.

## Authentication

Send the key on every `/api` request:

```
Authorization: Bearer <api-key>
```

Keys map to the user accountable for the runs they submit. Configure them under
`Auth`:

```json
{
  "Auth": {
    "Keys": [
      { "Token": "dev-local-key", "User": "local-developer" }
    ]
  }
}
```

A single key is also accepted via `Auth:ApiKey` plus `Auth:User`. A request
without a recognized key is rejected with `401`. A request for a run the caller
does not own is rejected with `403`. When no keys are configured, every `/api`
request is unauthorized.

## Endpoints

### POST /api/runs

Submits a task and starts a run. The run is created with its own branch
(`scaffolder/{runId}`) and worktree checked out from the originating branch; the
agent loop runs in the background.

Request:

```json
{
  "repository_path": "C:/path/to/repo",
  "originating_branch": "main",
  "task": "add a license header to every source file",
  "model_source": "github-copilot"
}
```

`model_source` must be `github-copilot` or `microsoft-foundry`; any other value
returns `400`. The submitting user is taken from the bearer key, not the body.

Response `202 Accepted`:

```json
{ "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6", "status": "pending" }
```

A missing field returns `400`. An invalid repository or branch returns `400`,
and the run is recorded as failed rather than left stranded.

### GET /api/runs/{id}

Returns the current state of a run. `diff` is the worktree's changes against the
originating branch; it is populated only when the run is `completed`, `approved`,
or `declined`.

Response `200`:

```json
{
  "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6",
  "status": "completed",
  "model_source": "github-copilot",
  "started_at": "2026-06-07T21:09:45.7526712+00:00",
  "ended_at": "2026-06-07T21:09:52.103+00:00",
  "step_count": 4,
  "diff": "diff --git a/a.txt b/a.txt\n..."
}
```

Unknown id returns `404`. Status values: `pending`, `in_progress`, `completed`,
`failed`, `bounded`, `reviewing`, `approved`, `declined`.

### GET /api/runs/{id}/stream

Server-sent event stream of the run's events. Each frame carries the per-run
`sequence` as the SSE `id` and the event envelope as `data`:

```
id: 1
data: {"runId":"...","sequence":1,"type":"run.started","timestamp":"...","payload":{...}}

```

Reconnect with the `Last-Event-ID` header set to the last sequence you saw. The
backend replays only events after that sequence from the durable log, then
continues live. Delivery is at-least-once; deduplicate by `sequence`. Replay works
across process restarts while the run is retained.

`Content-Type: text/event-stream`.

### GET /api/runs/{id}/events

Returns the durable event log as a JSON array. Query parameter `afterSequence`
(default `-1`) returns only events after that sequence.

Response `200`:

```json
[
  { "runId": "...", "sequence": 1, "type": "run.started", "timestamp": "...", "payload": { } },
  { "runId": "...", "sequence": 2, "type": "agent.message", "timestamp": "...", "payload": { "text": "..." } }
]
```

### POST /api/runs/{id}/review

Records a human decision. Only the run owner may submit. The run must be
`completed` and must not already carry a decision.

Request:

```json
{ "approved": true }
```

On approval the worktree is merged into the originating branch only if its tree
hash still matches the approved hash. On conflict, the originating branch is left
unchanged, a `merge.failed` event is recorded, and the worktree is preserved.

Response `200`:

```json
{ "run_id": "...", "status": "approved", "merge_result": "merged:34c09ee..." }
```

`merge_result` is `merged:{hash}` on a clean merge, `conflict:{reason}` when the
merge could not be applied, or `null` for a decline. A decision that cannot be
applied (run not awaiting review, or a decision already recorded) returns `409`.

## Event taxonomy

Every event shares the envelope `runId`, `sequence`, `type`, `timestamp`,
`payload`. Tool events also carry `callId`. `timestamp` is informational and must
not be used to order events; order by `sequence`.

| Type | Payload fields |
|------|----------------|
| `run.started` | `submitting_user`, `model_source`, `repository_path`, `originating_branch` |
| `run.completed` | `step_count` |
| `run.failed` | `reason` |
| `run.bounded` | `limit_type` (`step-count` or `wall-clock`), `step_count` |
| `agent.message` | `text` |
| `tool.call` | `path`, `operation` (`read` or `write`) |
| `tool.result` | `path`, `bytes_read_or_written` |
| `tool.rejected` | `path`, `reason` |
| `tool.error` | `path`, `error_message` |
| `review.requested` | `tree_hash` |
| `review.approved` | `tree_hash`, `approved_by` |
| `review.declined` | `declined_by` |
| `merge.completed` | `merged_commit_hash` |
| `merge.failed` | `reason` |

`tool.result`, `tool.rejected`, and `tool.error` echo the `callId` of their
`tool.call`.

## Persistence

Three SQLite tables back the API, created on startup with WAL enabled:

- `run_events` — the append-only event log, keyed by `(run_id, sequence)`. The
  sequence is allocated server-side inside the write transaction. Triggers reject
  any `UPDATE` or `DELETE`, so the log is strictly append-only.
- `runs` — run records with the mutable lifecycle fields (status, timing,
  worktree location, committed tree hash).
- `run_operational_records` — one record per run for compliance consumers,
  holding the submitting user, model source, timing, outcome, and the policy
  decisions enforced during the run.

The database path defaults to `scaffolder.db` in the application data directory
and is overridable with `Database:Path`. Worktrees default to a `worktrees`
subfolder there, overridable with `Worktrees:BasePath`. Neither uses the system
temp directory. The same build runs locally and in the cloud; all locations and
keys come from configuration.

## Configuration keys

| Key | Default | Purpose |
|-----|---------|---------|
| `Database:Path` | `scaffolder.db` in app data dir | SQLite database file |
| `Worktrees:BasePath` | `worktrees` in app data dir | Worktree root |
| `Git:Author:Name` | `Scaffolder` | Commit and merge author name |
| `Git:Author:Email` | `scaffolder@localhost` | Commit and merge author email |
| `Auth:Keys` | none | Array of `{ Token, User }` API keys |
| `Auth:ApiKey` / `Auth:User` | none | Single-key alternative |
