# API reference

The Scaffolder backend is the single source of truth for run lifecycle, streaming, review, and merge. Every client is a thin layer over these endpoints.

- Base path: `/api`
- Authentication: bearer API key on every request
- Event ordering: use `sequence`, not `timestamp`

## Authentication

Send the API key on every `/api` request:

```http
Authorization: Bearer <api-key>
```

Keys map to the user accountable for the runs they submit. You can configure multiple keys through `Auth:Keys`, or one key through `Auth:ApiKey` and `Auth:User`.

```json
{
  "Auth": {
    "Keys": [
      { "Token": "dev-local-key", "User": "local-developer" }
    ]
  }
}
```

A request without a recognized key returns `401 Unauthorized`. A request for a run the caller does not own returns `403 Forbidden`. When no keys are configured, every `/api` request is unauthorized.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/runs` | Submit a task and start a run |
| `GET` | `/api/runs/{id}` | Get current run state |
| `GET` | `/api/runs/{id}/stream` | Stream ordered run events over SSE |
| `POST` | `/api/runs/{id}/review` | Record an approve or decline decision |

### POST /api/runs

Submits a task and starts a run. The API creates a dedicated branch (`scaffolder/{runId}`), provisions a git worktree from the originating branch, records `run.started`, and starts the agent loop in the background.

Request:

```json
{
  "repository_path": "C:/path/to/repo",
  "originating_branch": "main",
  "task": "add a license header to every source file",
  "model_source": "github-copilot"
}
```

`model_source` must be `github-copilot` or `microsoft-foundry`. Any other value returns `400 Bad Request`. The submitting user comes from the bearer key, not the request body.

Response `202 Accepted`:

```json
{ "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6", "status": "pending" }
```

Validation failures return `400 Bad Request`. Invalid repositories or branches are recorded as failed runs so they do not stay stranded.

### GET /api/runs/{id}

Returns the current state of a run. Only the submitting user may access their own runs; non-owners receive `403 Forbidden`.

Response `200 OK`:

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

Unknown ids return `404 Not Found`. Status values are `pending`, `in_progress`, `completed`, `failed`, `bounded`, `reviewing`, `approved`, and `declined`.

### GET /api/runs/{id}/stream

Streams the run's events over SSE. Requires a valid bearer key and run ownership — a non-owner receives `404` (no existence leak). Each frame carries the per-run `sequence` as the SSE `id` and the event payload as `data`:

```text
id: 3
event: agent.message.delta
data: {"delta":"Hello","messageId":"msg-001"}

id: 4
event: run.completed
data: {}

event: done
data: {}
```

Event types emitted on the stream:

| Event | Payload |
| --- | --- |
| `agent.message.delta` | `{"delta":"...","messageId":"..."}` — incremental token |
| `agent.message` | `{"messageId":null,"content":"..."}` — complete message (restart fallback) |
| `run.completed` | `{}` |
| `run.failed` | `{"message":"The agent encountered an internal error."}` |

The stream ends with a synthetic `done` frame (no `id`) after the terminal event.

Set `Last-Event-ID` to the last sequence you received. The server resumes from that point in the in-memory event buffer. Reconnection works while the run's entry is retained in memory (up to 256 completed runs, entries evicted after approximately two hours of inactivity for in-progress runs). Delivery is at least once; deduplicate by `sequence`.

After a process restart, the in-memory event history is lost. If the run already completed, the endpoint replays the stored final result as a single `agent.message` event and closes the stream. If the run was still in progress, restart recovery marks it as failed and the stream returns `done` immediately with no events.

Response headers:

- `Content-Type: text/event-stream`
- `Cache-Control: no-cache`
- `Connection: keep-alive`

### POST /api/runs/{id}/review

Records a human review decision. Only the run owner may submit a decision. The run must be `completed` and must not already have a recorded decision.

Request:

```json
{ "approved": true }
```

On approval, the API verifies the approved tree hash, then attempts the merge back into the originating branch. If the branch diverged or the merge conflicts, the originating branch stays unchanged, `merge.failed` is recorded, and the worktree is preserved for inspection.

Response `200 OK`:

```json
{ "run_id": "...", "status": "approved", "merge_result": "merged:34c09ee..." }
```

`merge_result` is `merged:{hash}` on success, `conflict:{reason}` when the merge cannot be applied, or `null` for a decline. Rejected review attempts return `409 Conflict`.

## Event types on the stream

Events emitted by the agent runtime during a run:

| Type | Payload fields |
| --- | --- |
| `agent.message.delta` | `delta`, `messageId` |
| `agent.message` | `content`, `messageId` (restart fallback only) |
| `run.completed` | (empty) |
| `run.failed` | `message` |

The `done` frame (no `id` field) signals the end of the stream.

## Persistence

One SQLite table backs the API, created on startup with WAL enabled:

| Table | Purpose |
| --- | --- |
| `runs` | Run records with status, timing, submitting user, task, model source, and the final result text |

The run's event stream is held in memory by `RunStreamStore` and is not persisted to SQLite. After a process restart, the granular event history is unavailable — only the final `result` text survives. A durable append-only event log (`run_events`) is specified but not yet implemented.

## Configuration keys

### Core storage and git keys

| Key | Default | Purpose |
| --- | --- | --- |
| `Database:Path` | `scaffolder.db` in the app data directory | SQLite database file |
| `Worktrees:BasePath` | `worktrees` in the app data directory | Root folder for run worktrees |
| `Git:Author:Name` | `Scaffolder` | Author name for commits and merges |
| `Git:Author:Email` | `scaffolder@localhost` | Author email for commits and merges |
| `RunBounds:MaxSteps` | `50` | Maximum tool-call steps before `run.bounded` |
| `RunBounds:MaxMinutes` | `10` | Maximum wall-clock duration in minutes |

### Authentication keys

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:Keys` | none | Array of `{ Token, User }` API keys |
| `Auth:ApiKey` | none | Single-key alternative |
| `Auth:User` | none | User paired with `Auth:ApiKey` |

### Provider keys

| Key | Default | Purpose |
| --- | --- | --- |
| `Providers:GitHubCopilot:ApiKey` | none | GitHub Copilot provider credential |
| `Providers:GitHubCopilot:Endpoint` | `https://api.githubcopilot.com` | GitHub Copilot base URL |
| `Providers:GitHubCopilot:Model` | `gpt-4o` | GitHub Copilot model name |
| `Providers:MicrosoftFoundry:ApiKey` | none | Microsoft Foundry credential |
| `Providers:MicrosoftFoundry:Endpoint` | none | Microsoft Foundry endpoint |
| `Providers:MicrosoftFoundry:Deployment` | none | Microsoft Foundry deployment name |

Some older samples still use `Providers:Foundry` and `DeploymentName`. The runtime currently reads `Providers:MicrosoftFoundry` and `Deployment`.
