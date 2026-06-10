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

`repository_path` must be an absolute local filesystem path. It is canonicalized
via `Path.GetFullPath` on the server before use. UNC paths (`\\server\share`),
device paths (`\\?\`, `\\.\`), drive-relative paths (`C:foo`), relative paths,
and paths containing NTFS Alternate Data Streams are all rejected with `400`.

When `Runs:AllowedRepositoryRoots` is configured (non-empty string array), the
server resolves symlinks/junctions on the submitted path and verifies the result
falls within one of the allowed roots. Paths that resolve outside the allowlist
return `400` with an error. The default is permissive: any valid local absolute
path is accepted when no allowlist is configured.

Response `202 Accepted`:

```json
{ "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6", "status": "pending" }
```

A missing field returns `400`. An invalid `repository_path` (not a git repository)
or a non-existent `originating_branch` also returns `400` with a descriptive
`error` field. The run is recorded as failed rather than left stranded.

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

Server-sent event stream of the run's events. Requires a valid bearer key and
run ownership — non-owners receive `404` (no existence leak). Each frame carries
the per-run `sequence` as the SSE `id` and the event payload as `data`:

```
id: 3
event: agent.message.delta
data: {"delta":"Hello","messageId":"msg-001"}

id: 4
event: run.completed
data: {}

event: done
data: {}
```

Event types:

| Event | Payload |
|-------|---------|
| `agent.message.delta` | `{"delta":"...","messageId":"..."}` — incremental token |
| `agent.message` | `{"messageId":null,"content":"..."}` — complete message (restart fallback) |
| `run.completed` | `{}` |
| `run.failed` | `{"message":"The agent encountered an internal error."}` |

The stream ends with a synthetic `done` frame after the terminal event.

Reconnect with the `Last-Event-ID` header set to the last sequence you saw. The
server resumes from that point in the in-memory event buffer. Reconnection works
while the run's entry is retained in memory (up to 256 completed runs; in-progress
entries evicted after approximately two hours). Delivery is at-least-once;
deduplicate by `sequence`.

After a process restart the in-memory history is lost. If the run already
completed, the endpoint replays the stored final result as a single
`agent.message` event and closes. If the run was still in progress, restart
recovery marks it as failed and the stream returns `done` with no events.

`Content-Type: text/event-stream`.

### GET /api/runs/{id}/events

Not yet implemented. Planned as a JSON endpoint over a durable append-only event
log (FR-022). Currently returns `404`.

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

## Event types on the stream

Events emitted by the agent runtime during a run:

| Type | Payload fields |
|------|----------------|
| `agent.message.delta` | `delta`, `messageId` |
| `agent.message` | `content`, `messageId` (restart fallback only) |
| `run.completed` | (empty) |
| `run.failed` | `message` |

The `done` frame (no `id` field) signals the end of the stream.

## Persistence

One SQLite table backs the API, created on startup with WAL enabled:

- `runs` — run records with status, timing, submitting user, task, model source,
  and the final result text.

The run's event stream is held in memory by `RunStreamStore` and is not persisted
to SQLite. After a process restart, the granular event history is unavailable —
only the final `result` text survives in the `runs` table. A durable append-only
event log (`run_events`) is specified (FR-022) but not yet implemented.

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
| `Runs:AllowedRepositoryRoots` | `[]` (permissive) | String array of allowed parent directories for `repository_path`. When empty, any local absolute path is accepted. Shared or exposed deployments MUST configure this to restrict which repositories can be targeted. |
