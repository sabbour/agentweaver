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
| `GET` | `/api/runs/{id}` | Get current run state and diff |
| `GET` | `/api/runs/{id}/stream` | Stream ordered run events over SSE |
| `GET` | `/api/runs/{id}/events` | Read the durable event log as JSON |
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

Returns the current state of a run. `diff` contains the worktree changes against the originating branch after the run reaches `completed`, `approved`, or `declined`.

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

Streams the run's events as server-sent events. Each frame carries the per-run `sequence` as the SSE `id` and the event envelope as `data`.

```text
id: 1
data: {"runId":"...","sequence":1,"type":"run.started","timestamp":"...","payload":{...}}

```

Set the `Last-Event-ID` request header to reconnect after the last sequence you saw. The backend replays only later events from the durable log, then continues live. Delivery is at least once, so clients should deduplicate by `sequence`.

Response headers:

- `Content-Type: text/event-stream`
- `Cache-Control: no-cache`

### GET /api/runs/{id}/events

Returns the durable event log as a JSON array. Use `afterSequence` to fetch only events after a known cursor.

Request example:

```text
GET /api/runs/f36800fd-f2f8-418c-958e-aae3e4921ba6/events?afterSequence=12
```

Response `200 OK`:

```json
[
  { "runId": "...", "sequence": 1, "type": "run.started", "timestamp": "...", "payload": {} },
  { "runId": "...", "sequence": 2, "type": "agent.message", "timestamp": "...", "payload": { "text": "..." } }
]
```

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

## Event taxonomy summary

Every event shares the envelope `runId`, `sequence`, `type`, `timestamp`, and `payload`. Tool events also carry `callId`. `timestamp` is informational only and must not be used to order events.

| Type | Payload fields |
| --- | --- |
| `run.started` | `submitting_user`, `model_source`, `repository_path`, `originating_branch` |
| `run.completed` | `step_count` |
| `run.failed` | `reason` |
| `run.bounded` | `limit_type`, `step_count` |
| `agent.message` | `text` |
| `tool.call` | `path`, `operation` |
| `tool.result` | `path`, `bytes_read_or_written` |
| `tool.rejected` | `path`, `reason` |
| `tool.error` | `path`, `error_message` |
| `review.requested` | `tree_hash` |
| `review.approved` | `tree_hash`, `approved_by` |
| `review.declined` | `declined_by` |
| `merge.completed` | `merged_commit_hash` |
| `merge.failed` | `reason` |

`tool.result`, `tool.rejected`, and `tool.error` echo the `callId` of their originating `tool.call`.

For a fuller description of when each event fires, see [Events reference](/reference/events).

## Persistence

Three SQLite tables back the API, created on startup with WAL enabled:

| Table | Purpose |
| --- | --- |
| `run_events` | Append-only event log keyed by `(run_id, sequence)` |
| `runs` | Mutable run record with status, timings, worktree path, and committed tree hash |
| `run_operational_records` | One record per run for compliance, debugging, and capacity analysis |

`run_events` is strictly append-only. The API allocates `sequence` server-side inside the write transaction, and SQLite triggers reject any `UPDATE` or `DELETE`.

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
