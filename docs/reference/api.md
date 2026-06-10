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

Submits a task and starts a run. The API creates a dedicated branch (`scaffolder/{runId}`), provisions a git worktree from the originating branch, and starts the agent loop in the background.

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

`repository_path` must be an absolute local filesystem path. The server canonicalizes it with `Path.GetFullPath` before storing it on the run record. UNC paths (`\\server\share`, `//server/share`), device paths (`\\?\`, `\\.\`), drive-relative paths (`C:foo`), relative paths, and NTFS Alternate Data Streams are rejected with `400`.

When `Runs:AllowedRepositoryRoots` is configured (non-empty string array), the server resolves the canonical path through symlinks and junctions and verifies that the resolved location is inside one of the allowed roots. Paths outside the allowlist return `400`. By default no allowlist is configured and any valid local absolute path is accepted. Shared, exposed, or multi-tenant deployments MUST set an allowlist to prevent users from targeting arbitrary repositories on the server filesystem.

Response `202 Accepted`:

```json
{ "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6", "status": "in_progress" }
```

Validation failures return `400 Bad Request`. If `repository_path` is not a valid git repository or `originating_branch` does not exist in that repository, the response is `400` with an `error` field describing the problem. Invalid repositories or branches are recorded as failed runs so they do not stay stranded.

### GET /api/runs/{id}

Returns the current state of a run. Only the submitting user may access their own runs; non-owners receive `403 Forbidden`.

Response `200 OK`:

```json
{
  "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6",
  "status": "awaiting_review",
  "model_source": "github-copilot",
  "started_at": "2026-06-07T21:09:45.7526712+00:00",
  "ended_at": "2026-06-07T21:09:52.103+00:00",
  "step_count": 4,
  "tree_hash": "a1b2c3d4e5f6...",
  "diff": "diff --git a/a.txt b/a.txt\n..."
}
```

Unknown ids return `404 Not Found`. Status values are `pending`, `in_progress`, `awaiting_review`, `merging`, `merged`, `declined`, `merge_failed`, `failed`, and `completed`. `completed` is reached when the agent turn produced no file changes (no review gate is entered on that path).

### GET /api/runs/{id}/stream

Streams the run's events over SSE. Requires a valid bearer key and run ownership — a non-owner receives `404` (no existence leak). Each frame carries the per-run `sequence` as the SSE `id` and the event payload as `data`:

```text
id: 3
event: agent.message.delta
data: {"delta":"Hello","messageId":"msg-001"}

id: 4
event: run.completed
data: {"result":"no_changes"}

event: done
data: {}
```

The stream ends with a synthetic `done` frame (no `id`) after the terminal event.

Set `Last-Event-ID` to the last sequence you received. The server resumes from that point in the in-memory event buffer. Reconnection works while the run's entry is retained in memory (up to 256 completed runs; in-progress entries are evicted after approximately two hours of inactivity). `awaiting_review` runs and any run actively being merged are exempt from inactivity eviction — entries for those runs stay in memory until a terminal review decision is recorded. After a process restart, stream entries for `awaiting_review` runs are re-created so the review endpoint can still emit events to reconnected clients; any run interrupted mid-merge is reverted to `awaiting_review` and also gets a fresh entry.

After a process restart, the in-memory event history is lost. If the run already completed, the endpoint replays the stored final result as a single `agent.message` event and closes the stream. If the run was still in progress at restart, recovery marks it as failed and the stream returns `done` immediately with no events.

Response headers:

- `Content-Type: text/event-stream`
- `Cache-Control: no-cache`
- `Connection: keep-alive`

### POST /api/runs/{id}/review

Records a human review decision. Only the run owner may submit a decision. Non-owners receive `403 Forbidden`.

Request:

```json
{ "approved": true }
```

**Primary path (normal operation)**

In normal operation the API hands the decision to the background MAF workflow and returns immediately:

- **Approve** — `200 OK`, `status: "merging"`. The merge runs asynchronously inside the workflow. Watch the SSE stream for `review.approved` followed by either `merge.completed` or `merge.failed` to learn the outcome.
- **Decline** — `200 OK`, `status: "declined"`. The workflow terminates; `review.declined` is emitted on the stream.

```json
{ "run_id": "...", "status": "merging", "merge_result": null }
{ "run_id": "...", "status": "declined", "merge_result": null }
```

**Idempotent re-POST**

If the run has already reached a matching terminal state, the endpoint returns the current state rather than an error:

- Re-approving an already-`merged` run returns `200 OK` with `status: "merged"` and the stored `merge_result`.
- Re-declining an already-`declined` run returns `200 OK` with `status: "declined"`.

**Error responses**

| Status | Condition |
| --- | --- |
| `403 Forbidden` | The caller does not own the run |
| `404 Not Found` | No run found for the given id |
| `409 Conflict` | The run is not in `awaiting_review` status (and the decision does not match an already-terminal state), or the review decision was already consumed by a concurrent POST |

A `409` from a duplicate or concurrent POST has no body. A `409` from a wrong-status run includes an error message:

```json
{ "error": "Run is in status 'in_progress' and cannot be reviewed." }
```

**Direct fallback path (post-restart recovery)**

After a server restart, if no workflow checkpoint is available to resume, the endpoint executes the merge or decline synchronously and returns the final outcome directly:

- **Merge succeeds** — `200 OK`, status `merged`. The run's worktree branch is merged into the originating branch. `merge_result` is `merged:{commit-hash}`. If the originating branch is currently checked out and the tree is clean, the branch ref is advanced and the working tree is updated via a hard reset. If it is not checked out, only the branch ref is advanced. On success the worktree is torn down: its physical directory is deleted first, the admin entry is pruned, then the branch is removed.

- **Blocked (retriable)** — `409 Conflict`, status `awaiting_review`. No git mutations occurred. The run stays at the review gate and can be approved again once the condition is resolved. Causes include: uncommitted changes to tracked files, staged changes in the index, untracked files that would be overwritten by the merge, a merge or rebase already in progress in the working tree, the repository lock being held by another concurrent request, or a concurrent approve that already won the CAS gate. Body:

  ```json
  { "error": "there are uncommitted changes to tracked files", "status": "awaiting_review" }
  ```

- **Terminal conflict** — `200 OK`, status `merge_failed`. The originating branch has diverged with conflicts that require human resolution, or the tree hash stored at review time no longer matches the worktree branch. The originating branch is unchanged and the worktree is preserved. `merge_result` is `conflict:{reason}`.

- **Decline** — `200 OK`, status `declined`, `merge_result: null`.

```json
{ "run_id": "...", "status": "merged", "merge_result": "merged:34c09ee..." }
{ "run_id": "...", "status": "merge_failed", "merge_result": "conflict:The originating branch has diverged..." }
{ "run_id": "...", "status": "declined", "merge_result": null }
```

See [events.md](events.md) for the event types emitted on the stream for each outcome.

## Sandbox policy endpoints

These endpoints read and write the per-project sandbox execution policy stored at `.scaffolder/sandbox.yml` in the project repository root. Sandbox policies control whether shell execution is enabled, which commands require human approval, and output handling options. See [sandbox-setup.md](sandbox-setup.md) for setup and [architecture/sandboxed-execution.md](../architecture/sandboxed-execution.md) for the full design.

### GET /api/sandbox-policy

Returns the sandbox policy for the given repository path by reading `{repository_path}/.scaffolder/sandbox.yml`. If the file does not exist, returns the default policy.

Query parameters:

| Parameter | Required | Description |
| --- | --- | --- |
| `repository_path` | Yes | Absolute path to the repository |

Response `200 OK`:

```json
{
  "repository_path": "C:/repos/myproject",
  "shell_enabled": true,
  "allowed_repository_roots": [],
  "destructive_command_patterns": [
    "rm -rf", "del /s", "format ", "mkfs", "dd if=",
    "git push --force", "git reset --hard"
  ],
  "require_approval_for_all_shell": false,
  "redact_pii": true,
  "max_output_bytes": 4194304
}
```

Missing or malformed `repository_path` returns `400 Bad Request`.

### PUT /api/sandbox-policy

Creates or replaces the sandbox policy for a repository path by writing `{repository_path}/.scaffolder/sandbox.yml`. The entire policy is replaced on each PUT; there is no partial-update merge. After a PUT, the operator should commit the updated file to the project repository to record the change in version history.

Request body (all fields required):

```json
{
  "repository_path": "C:/repos/myproject",
  "shell_enabled": true,
  "allowed_repository_roots": [],
  "destructive_command_patterns": ["rm -rf", "del /s"],
  "require_approval_for_all_shell": false,
  "redact_pii": true,
  "max_output_bytes": 4194304
}
```

Response `200 OK` returns the stored policy. Validation failures return `400 Bad Request`.

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `repository_path` | string | — | Required. Lookup key. Must be an absolute path. |
| `shell_enabled` | bool | `true` | When `false`, `run_command` is excluded from the model's tool list for this project and denied by the governance gate. |
| `allowed_repository_roots` | string[] | `[]` | Additional paths mounted read-only inside the sandbox. |
| `destructive_command_patterns` | string[] | see default | Command substrings that trigger a `shell.approval_required` pause. |
| `require_approval_for_all_shell` | bool | `false` | When `true`, every shell command requires approval regardless of pattern matching. |
| `redact_pii` | bool | `true` | When `true`, emails and IP addresses are removed from command output in addition to secrets. |
| `max_output_bytes` | int | `4194304` | Output cap in bytes. Exceeded output is truncated and marked `output_truncated: true`. |

## Event types on the stream

The full event taxonomy — types, payload fields, and per-event descriptions — is in [events.md](events.md).

The `done` frame (no `id` field) signals the end of the stream.

### Sandbox event types

The following event types are added by the sandboxed execution feature. They appear on the existing SSE stream alongside the base event types.

#### sandbox.selected

Emitted at run start after the executor selection probe completes. Present on every run.

```json
{
  "backend": "processcontainer",
  "is_real_isolation": true,
  "reason": "processcontainer supported"
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `backend` | string | One of `processcontainer`, `wsl-lxc`, `lxc-native-linux`, `passthrough-deny` |
| `is_real_isolation` | bool | `true` when the backend provides real process isolation. `false` for `passthrough-deny`. Shell execution is denied when `false`. |
| `reason` | string | Human-readable reason from the platform probe or selection logic |

#### sandbox.warning

Emitted when the selected executor has a known limitation that operators should be aware of.

```json
{
  "category": "network-unrestricted",
  "message": "Sandbox running with unrestricted network on Windows (allowlist enforcement unavailable). Data exfiltration surface is open.",
  "backend": "processcontainer"
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `category` | string | Currently only `network-unrestricted` — the Windows AppContainer backend cannot enforce a network allowlist |
| `message` | string | Human-readable description |
| `backend` | string | The backend that produced the warning |

#### shell.approval_required

Emitted when a `run_command` invocation matches a destructive command pattern or when `require_approval_for_all_shell` is `true`. The run pauses pending human approval.

```json
{
  "request_id": "apr-f36800fd",
  "command_length": 42,
  "command_hash": "sha256:a1b2c3...",
  "message": "Command matches destructive pattern 'rm -rf'. Approve to proceed."
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `request_id` | string | Unique ID for this approval request. Used by the (pending) approval endpoint. |
| `command_length` | int | Length of the command line in characters |
| `command_hash` | string | SHA-256 of the command line, prefixed with `sha256:` |
| `message` | string | Human-readable reason the approval was triggered |

The approval API endpoint (`POST /api/runs/{id}/shell-approve`) is not yet implemented (T017-api). Runs that reach this event will pause indefinitely.

#### tool.output

Emitted for each chunk of stdout or stderr produced by a sandboxed `run_command` invocation during streaming execution.

```json
{
  "stream": "stdout",
  "data": "Hello from sandbox\n"
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `stream` | string | `"stdout"` or `"stderr"` |
| `data` | string | A line or chunk of output from the command. PII and secrets are redacted per the sandbox policy. |

#### tool.exec_result

Reports the terminal outcome of a `run_command` invocation. **Planned — not yet emitted separately from `tool.result`.**

```json
{
  "exit_code": 0,
  "timed_out": false,
  "output_truncated": false
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `exit_code` | int | Process exit code. `-1` when the command timed out or was denied. |
| `timed_out` | bool | `true` when the command was terminated because it exceeded the configured time limit |
| `output_truncated` | bool | `true` when captured output exceeded `max_output_bytes` and was cut off |

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

### Security keys

| Key | Default | Purpose |
| --- | --- | --- |
| `Runs:AllowedRepositoryRoots` | `[]` (permissive) | String array of allowed parent directories for `repository_path`. Symlinks and junctions in the submitted path are resolved and the final location must fall within one of these roots. When empty (the default), any valid local absolute path is accepted. Shared, exposed, or multi-tenant deployments MUST configure this. |

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
