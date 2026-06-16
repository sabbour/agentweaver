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

### Runs

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/runs` | Submit a task and start a run |
| `GET` | `/api/runs/{id}` | Get current run state |
| `GET` | `/api/runs/{id}/stream` | Stream ordered run events over SSE |
| `POST` | `/api/runs/{id}/review` | Record an approve or decline decision |
| `POST` | `/api/runs/{id}/shell-approvals` | Approve a pending destructive shell command |
| `GET` | `/api/runs/{id}/history` | Replay persisted session events for terminal runs |
| `POST` | `/api/runs/{id}/commit` | Commit worktree changes and merge into originating branch |
| `POST` | `/api/runs/{id}/request-changes` | Request a revision cycle: agent rewrites in place |
| `GET` | `/api/runs/{id}/workspace` | List workspace files with change status and line counts |
| `GET` | `/api/runs/{id}/files` | List changed files (flat, with filter) |
| `GET` | `/api/runs/{id}/files/{path}` | Get diff or content for a specific file |
| `POST` | `/api/runs/{id}/tool-approvals` | Approve a pending tool call |
| `POST` | `/api/runs/{id}/tool-denials` | Deny a pending tool call |

### Projects

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects` | Create a project (blank or from GitHub) |
| `GET` | `/api/projects` | List all projects |
| `GET` | `/api/projects/{id}` | Get a project by id |
| `PATCH` | `/api/projects/{id}` | Rename a project |
| `PUT` | `/api/projects/{id}/provider-settings` | Update provider and model defaults |
| `POST` | `/api/projects/{id}/relink` | Relink a project to a moved directory |
| `DELETE` | `/api/projects/{id}` | Delete a project (record only; cancels active runs) |
| `GET` | `/api/projects/{id}/runs` | List runs for a project |
| `POST` | `/api/projects/{id}/runs` | Start a run within a project |

Run summary objects returned by `GET /api/projects/{id}/runs` include a `result` field (`"no_changes"` or `null`). When `result` is `"no_changes"`, the agent found no file changes to commit; the review and merge gates are skipped.

### Memory

Memory is scoped to projects. Decisions and memories feed the `MemoryContextCompiler`, which assembles a hierarchical context block injected into every agent run (boundaries → core context → learnings → session). Export writes to `.squad/decisions.md`, `.squad/agents/{name}/history.md`, `.agentweaver/context/boundaries.md`, and `.agentweaver/context/patterns.md`.

#### Decision Inbox

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/decisions/inbox` | Submit a decision or learning to the inbox |
| `GET` | `/api/projects/{id}/decisions/inbox` | List inbox entries (`?agent=`, `?type=`, `?status=`) |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/merge` | Merge a pending entry into decisions |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/reject` | Reject a pending entry |

#### Decisions

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/decisions` | Create a decision directly |
| `GET` | `/api/projects/{id}/decisions` | List decisions (`?type=`, `?agent=`) |
| `PUT` | `/api/projects/{id}/decisions/{decisionId}` | Update decision status/content |

#### Agent Memory

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/agents/{name}/memory` | Add a memory entry for an agent |
| `GET` | `/api/projects/{id}/agents/{name}/memory` | List agent memories (`?type=`, `?importance=`) |
| `GET` | `/api/projects/{id}/agents/{name}/memory/{memId}` | Get a single memory entry |
| `GET` | `/api/projects/{id}/memory` | Cross-agent memory search (`?type=`, `?tags=`) |

#### Sessions

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/sessions` | Start a new session (auto-ends existing) |
| `GET` | `/api/projects/{id}/sessions/current` | Get current open session |
| `PUT` | `/api/projects/{id}/sessions/current` | Update focus, summary, or end session |

#### Export / Import

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/memory/export` | Export DB memory → `.squad/` + `.agentweaver/context/` |
| `POST` | `/api/projects/{id}/memory/import` | Import `.squad/decisions/inbox/*.md` → DB |

### GitHub authentication

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/auth/github/device` | Start the GitHub device authorization flow |
| `POST` | `/api/auth/github/poll` | Poll the device flow for completion |
| `GET` | `/api/auth/github` | Get current GitHub authentication status |
| `POST` | `/api/auth/github/sign-out` | Sign out and delete the stored token |

### Team casting

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/casting/templates` | List available scenario groupings (team templates) |
| `GET` | `/api/catalog/roles` | List all available role definitions |
| `POST` | `/api/projects/{id}/casting/proposals` | Create a casting proposal |
| `GET` | `/api/projects/{id}/casting/proposals/{pid}` | Get a proposal |
| `PATCH` | `/api/projects/{id}/casting/proposals/{pid}` | Amend a proposal |
| `POST` | `/api/projects/{id}/casting/proposals/{pid}/confirm` | Confirm a proposal and create the team |
| `DELETE` | `/api/projects/{id}/casting/proposals/{pid}` | Reject a proposal |
| `GET` | `/api/projects/{id}/team` | Get team roster and layout metadata |
| `GET` | `/api/projects/{id}/team/members/{name}/charter` | Get a member's charter |
| `PUT` | `/api/projects/{id}/team/members/{name}/charter` | Replace a member's charter |
| `GET` | `/api/projects/{id}/team/members/{name}/history` | Get agent interaction history |
| `POST` | `/api/projects/{id}/team/members` | Add a team member |
| `DELETE` | `/api/projects/{id}/team/members/{name}` | Retire a team member |
| `PATCH` | `/api/projects/{id}/team/members/{name}` | Re-role a team member |
| `GET` | `/api/projects/{id}/team/sync` | Get pending .squad/ changes and change set hash |
| `POST` | `/api/projects/{id}/team/sync` | Commit pending .squad/ changes |

Team member objects include `is_built_in: true` for Scribe, Ralph, and Rai (case-insensitive). Built-in agents cannot be removed, re-roled, or directly run. Attempting to start a run with a built-in agent name returns `400 Bad Request`.

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

`model_source` must be `github-copilot`. Any other value returns `400 Bad Request`. The submitting user comes from the bearer key, not the request body.

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

### GET /api/runs/{id}/history

Replays persisted Copilot SDK session events for a terminal run. The session is identified by `scaffolder-run-{runId}`. Returns a JSON array of run events in stream order. Only available for terminal runs. Returns `404` if the run is not terminal or the session is not found.

### POST /api/runs/{id}/commit

Commits any remaining uncommitted worktree changes and immediately merges the worktree branch into the originating branch. The run must be in `awaiting_review`. Uses CAS `AwaitingReview → Committing → Merging` to prevent concurrent commits.

Response:

- `200 OK` `{ run_id, status: "merged", merge_result: "merged:{hash}" }` on success
- `200 OK` `{ run_id, status: "merge_failed", merge_result: "conflict:{reason}", conflicting_files: [...] }` on conflict
- `409 Conflict` `{ error, status: "awaiting_review" }` on retriable block (dirty working tree, concurrent request, etc.)
- `409 Conflict` `{ error }` if the run is not in `awaiting_review`

### POST /api/runs/{id}/request-changes

Requests a revision cycle. The agent is given the reviewer's comment and re-runs on the same worktree without creating a new branch. The run returns to `in_progress`.

Request body:

```json
{ "comment": "string" }
```

Response: `202 Accepted` with the updated run.

### GET /api/runs/{id}/workspace

Returns a tree of all files in the run's worktree (folders + files). Files include `path`, `is_folder`, `status` (added/modified/deleted, null for unchanged), `added_lines`, `removed_lines`. Returns `404` for terminal runs whose worktrees have been removed (failed/merged/declined/merge_failed). Returns empty array for `pending`. Returns `409` while the worktree does not exist for an active run.

### GET /api/runs/{id}/files

Flat list of changed files. Query param `filter`: `all` (committed + uncommitted), `committed`, `uncommitted`, `last-commit`. Returns `409` for non-terminal runs with no worktree.

### GET /api/runs/{id}/files/{path}

Returns diff and content for a specific file. Response includes `path`, `status`, `diff`, `content`, `is_binary`.

### POST /api/runs/{id}/tool-approvals

Approves a pending tool call.

Request:

```json
{ "request_id": "string", "scope": "once" | "run" | "always" | "tool" }
```

Scope values: `once` = this call only; `run` = all calls to the same tool+url this run; `always` = all calls this server session; `tool` = all calls to this tool regardless of url.

Response: `200 OK`.

### POST /api/runs/{id}/tool-denials

Denies a pending tool call.

Request:

```json
{ "request_id": "string" }
```

Response: `200 OK`.

## Sandbox policy endpoints

These endpoints read and write the per-project sandbox execution policy stored at `.scaffolder/settings.yml` in the project repository root. Sandbox policies control whether shell execution is enabled, which commands require human approval, and output handling options. See [sandbox-setup.md](sandbox-setup.md) for setup and [architecture/sandboxed-execution.md](../architecture/sandboxed-execution.md) for the full design.

### GET /api/sandbox-policy

Returns the sandbox policy for the given repository path by reading `{repository_path}/.scaffolder/settings.yml`. If the file does not exist, returns the default policy.

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

Creates or replaces the sandbox policy for a repository path by writing `{repository_path}/.scaffolder/settings.yml`. The entire policy is replaced on each PUT; there is no partial-update merge. After a PUT, the operator should commit the updated file to the project repository to record the change in version history.

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

The `run.outcome` event is emitted by the agent just before `run.completed` when the agent supports self-assessment. See [events.md](events.md) for the full payload.

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

The approval API endpoint (`POST /api/runs/{id}/shell-approvals`) records operator approval for a pending shell command. Use the `commandHash` from the `shell.approval_required` event as the request body's `command_hash`. Once approved, the model may retry the command and it will execute immediately.

```http
POST /api/runs/{id}/shell-approvals
Content-Type: application/json

{ "command_hash": "a1b2c3d4e5f6a1b2" }
```

Response `200 OK`:

```json
{ "run_id": "f36800fd-...", "command_hash": "a1b2c3d4e5f6a1b2", "approved": true }
```

Returns `400 Bad Request` when `command_hash` is missing or empty.

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

## Project endpoints

### POST /api/projects

Creates a new project. Set `origin` to `"blank"` to register a local directory as a project, or `"github"` to clone a GitHub repository into the working directory first.

Request:

```json
{
  "name": "my-project",
  "origin": "blank",
  "working_directory": "C:/repos/my-project",
  "default_provider": "github-copilot",
  "default_model_github_copilot": null,
  "default_model_microsoft_foundry": null
}
```

For a GitHub-origin project, add `"source_repository": "owner/repo"` and the server will clone the repository into `working_directory`.

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `name` | string | Yes | Display name |
| `origin` | string | Yes | `"blank"` or `"github"` |
| `working_directory` | string | Yes | Absolute local path for the project |
| `source_repository` | string | When `origin` is `"github"` | GitHub repository in `owner/repo` format |
| `default_provider` | string | No | `"github-copilot"` or `"microsoft-foundry"`. Falls back to the runtime default when omitted. |
| `default_model_github_copilot` | string | No | Model name override for the GitHub Copilot provider |
| `default_model_microsoft_foundry` | string | No | Model name override for the Microsoft Foundry provider |

Response `201 Created` returns a project object:

```json
{
  "project_id": "a1b2c3d4-...",
  "name": "my-project",
  "origin": "blank",
  "source_repository": null,
  "working_directory": "C:/repos/my-project",
  "default_branch": "main",
  "owner": "local-developer",
  "default_provider": "github-copilot",
  "default_model_github_copilot": null,
  "default_model_microsoft_foundry": null,
  "available": true,
  "state": "active",
  "created_at": "2026-06-07T21:00:00+00:00",
  "updated_at": "2026-06-07T21:00:00+00:00"
}
```

`available` is `true` when the working directory exists on the server filesystem. `state` is `"active"` or `"deleting"`.

Validation failures return `400 Bad Request`.

### GET /api/projects

Returns all projects owned by the authenticated user. Each entry uses the same shape as the `POST /api/projects` response.

### GET /api/projects/{id}

Returns a single project. Returns `404 Not Found` when no project exists for the given id.

### PATCH /api/projects/{id}

Renames a project.

Request:

```json
{ "name": "new-name" }
```

Response `204 No Content` on success. `400` when `name` is missing. `404` when the project does not exist.

### PUT /api/projects/{id}/provider-settings

Updates the provider and model defaults for a project.

Request:

```json
{
  "default_provider": "microsoft-foundry",
  "default_model_github_copilot": null,
  "default_model_microsoft_foundry": "gpt-4o"
}
```

All fields are optional. Omitting a field leaves it unchanged. Response `204 No Content` on success.

### POST /api/projects/{id}/relink

Updates the working directory path for a project. Use this after moving a repository to a new location.

Request:

```json
{ "working_directory": "D:/new-location/my-project" }
```

Response `204 No Content` on success. `400` when the new path is missing or not a valid git repository.

### DELETE /api/projects/{id}

Deletes the project record. Does not touch the working directory or git history. Active runs for the project are cancelled; each cancelled run emits a `run.cancelled` event on its stream.

Requires the query parameter `confirm=true`:

```
DELETE /api/projects/a1b2c3d4-...?confirm=true
```

Without `confirm=true` the request returns `400 Bad Request`. Response `204 No Content` on success.

### GET /api/projects/{id}/runs

Lists all runs for a project. Returns a JSON array. Each entry includes `agent_name` identifying which team member executed the run (null when the run was not started by a cast team member):

```json
[
  {
    "run_id": "f36800fd-...",
    "status": "merged",
    "model_source": "github-copilot",
    "model_id": null,
    "task": "add license headers",
    "agent_name": "Aria",
    "started_at": "2026-06-07T21:09:45+00:00",
    "ended_at": "2026-06-07T21:10:12+00:00"
  }
]
```

### POST /api/projects/{id}/runs

Starts a run within a project. The project's working directory and default branch are used unless overridden in the request body.

Request:

```json
{
  "task": "add a license header to every source file",
  "model_source": "github-copilot",
  "model_id": null,
  "base_branch": "main",
  "agent_name": "Aria"
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `task` | string | Yes | Task description for the agent |
| `model_source` | string | No | `"github-copilot"` or `"microsoft-foundry"`. Falls back to the project default, then the runtime default. |
| `model_id` | string | No | Model override. Falls back to the project default for the resolved provider, then `null`. |
| `base_branch` | string | No | Branch to run from. Falls back to the project's `default_branch`. |
| `agent_name` | string | No | Name of a cast team member. When provided, that member's charter is injected as the agent's system prompt. |

Provider and model resolution order:
1. Value in the request body
2. Project default (`PUT /api/projects/{id}/provider-settings`)
3. Runtime default (`appsettings.json`)

Response `202 Accepted`:

```json
{ "run_id": "f36800fd-...", "status": "in_progress" }
```

`409 Conflict` with `error: "project_deleting"` when the project is being deleted.
`409 Conflict` with `error: "workspace_unavailable"` when the working directory is not accessible. Use `POST /api/projects/{id}/relink` to reconnect the project to its new location.

## GitHub authentication endpoints

GitHub authentication allows the GitHub Copilot provider to use a token obtained through the OAuth device flow rather than a static API key.

### POST /api/auth/github/device

Starts the GitHub device authorization flow for the calling user.

Response `200 OK`:

```json
{
  "user_code": "XXXX-XXXX",
  "verification_uri": "https://github.com/login/device",
  "expires_in": 900,
  "interval": 5
}
```

Direct the user to `verification_uri` and ask them to enter `user_code`. Then poll `POST /api/auth/github/poll` at the returned `interval` (seconds) until the flow completes or expires.

### POST /api/auth/github/poll

Polls the active device flow for the calling user.

Response `200 OK`:

```json
{ "status": "pending", "login": null }
```

| `status` value | Meaning |
| --- | --- |
| `pending` | User has not yet authorized — keep polling |
| `success` | Authorization granted; `login` contains the GitHub username |
| `expired` | The device code expired before the user authorized |
| `denied` | The user actively denied the request |

On `success` the token is stored server-side and used automatically for subsequent Copilot provider calls.

### GET /api/auth/github

Returns the current GitHub authentication state for the calling user.

Response `200 OK`:

```json
{ "status": "signed_in", "login": "octocat" }
```

| `status` value | Meaning |
| --- | --- |
| `signed_in` | A valid token is stored; `login` contains the GitHub username |
| `signed_out` | The user explicitly signed out |
| `never_signed_in` | No sign-in has been completed for this user |

### POST /api/auth/github/sign-out

Deletes the stored GitHub token for the calling user. Response `204 No Content`.

## Team casting endpoints

The team casting API manages the full lifecycle of AI-assisted agent team composition: listing available scenario groupings, creating and amending casting proposals, confirming a proposal into a live team, and committing the resulting `.squad/` files back to the repository.

The provider for model-assisted casting is always GitHub Copilot. No provider field is accepted on casting requests.

### GET /api/casting/templates

Lists the available team templates (scenario groupings). Each template groups a curated set of agent roles suitable for a particular project type.

Response `200 OK`:

```json
[
  {
    "id": "quick-software-development",
    "title": "Quick Software Development",
    "description": "Lean team for rapid software delivery.",
    "roles": [
      {
        "id": "software-engineer",
        "title": "Software Engineer",
        "summary": "Implements features and fixes bugs.",
        "default_model": "gpt-4o"
      }
    ]
  }
]
```

### GET /api/catalog/roles

Returns all available role definitions from the catalog. Use the `id` values when creating proposals in `manual` mode or adding individual members.

Response `200 OK`: a JSON array of role objects, each with `id`, `title`, `summary`, and `default_model`.

### POST /api/projects/{id}/casting/proposals

Creates a casting proposal. Depending on `mode`, the server selects roles deterministically from a template, runs a model-assisted analysis, or accepts an explicit role list.

Request:

```json
{
  "mode": "scenario",
  "template_id": "quick-software-development",
  "universe": "star-wars",
  "team_size": null,
  "model_id": null,
  "goal": null,
  "role_ids": null
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `mode` | string | Yes | `"scenario"`, `"free_text"`, `"analysis"`, or `"manual"` |
| `template_id` | string | When `mode` is `"scenario"` | ID from `GET /api/casting/templates` |
| `goal` | string | When `mode` is `"free_text"` | Natural-language description of the team goal |
| `role_ids` | string[] | When `mode` is `"manual"` | Explicit list of role IDs from `GET /api/catalog/roles` |
| `universe` | string | No | Thematic universe name applied to agent personas (e.g. `"star-wars"`) |
| `team_size` | int | No | Desired number of team members; guides model-assisted modes |
| `model_id` | string | No | Model override for `free_text` and `analysis` modes |

For `"free_text"` and `"analysis"` modes the server runs a GitHub Copilot model to propose roles. All modes return the proposal synchronously once ready.

Response `200 OK` — a `CastProposalDto`:

```json
{
  "proposal_id": "prop-a1b2c3",
  "mode": "scenario",
  "universe": "star-wars",
  "run_id": null,
  "existing_team_present": false,
  "warnings": [],
  "rationale": "A balanced team for rapid software delivery.",
  "members": [
    {
      "proposed_name": "Han Solo",
      "role": {
        "id": "software-engineer",
        "title": "Software Engineer",
        "summary": "Implements features and fixes bugs.",
        "default_model": "gpt-4o"
      },
      "charter_markdown": "# Han Solo\n...",
      "is_named": true,
      "default_model": "gpt-4o",
      "justification": null
    }
  ]
}
```

`run_id` is populated for `free_text` and `analysis` modes. Use `GET /api/runs/{id}/stream` to follow the model run while the proposal is being generated; the proposal is ready when the run completes. `run_id` is `null` for `scenario` and `manual` modes, which resolve synchronously.

Error responses:

| Status | Error | Meaning |
| --- | --- | --- |
| `400` | — | `mode` is invalid, or a required mode-specific field is missing |
| `404` | — | Project not found |
| `409` | `project_unavailable` | The project's working directory is not accessible |
| `409` | `layout_conflict` | Both canonical and legacy `.squad/` layouts are present |

### GET /api/projects/{id}/casting/proposals/{pid}

Returns the current state of a proposal.

Response `200 OK` — a `CastProposalDto` (same shape as the `POST` response above).

Returns `404 Not Found` when no proposal exists for the given id.

### PATCH /api/projects/{id}/casting/proposals/{pid}

Amends a proposal by replacing its member list and/or universe. Use this to add, remove, or modify proposed members before confirming.

Request:

```json
{
  "universe": "marvel",
  "members": [
    {
      "proposed_name": "Tony Stark",
      "role": {
        "id": "software-engineer",
        "title": "Software Engineer",
        "summary": "Implements features and fixes bugs.",
        "default_model": "gpt-4o"
      },
      "charter_markdown": "# Tony Stark\n...",
      "is_named": true,
      "default_model": "gpt-4o",
      "justification": null
    }
  ]
}
```

Both `members` and `universe` are optional; omit either to leave it unchanged.

Response `200 OK` returns the updated `CastProposalDto`. Returns `404 Not Found` when the proposal does not exist.

### POST /api/projects/{id}/casting/proposals/{pid}/confirm

Confirms a proposal and materialises the team by writing `.squad/` files. For projects with an existing team, the `intent` field controls how the proposed team relates to the existing one.

Request:

```json
{
  "intent": "new"
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `intent` | string | Conditionally | Required when an existing team is detected. `"new"` replaces the team entirely; `"augment"` adds the proposed roles to the existing team; `"recast"` rewrites all existing charters using the proposed configuration. Omit when no existing team is present. |

Response `200 OK` — a `TeamDto` (same shape as `GET /api/projects/{id}/team`):

```json
{
  "project_name": "my-project",
  "universe": "star-wars",
  "layout": "canonical",
  "migration_available": false,
  "members": [
    {
      "name": "Han Solo",
      "role_title": "Software Engineer",
      "charter_path": ".squad/HanSolo/charter.md",
      "status": "active",
      "default_model": "gpt-4o",
      "is_named": true,
      "charter_created_at": "2026-06-07T21:00:00+00:00",
      "charter_updated_at": "2026-06-07T21:00:00+00:00"
    }
  ]
}
```

Notable error responses:

| Status | Error | Meaning |
| --- | --- | --- |
| `404` | — | Proposal or project not found |
| `409` | `requires_choice` | An existing team was detected and `intent` was not provided |
| `409` | `layout_conflict` | Both canonical and legacy `.squad/` layouts are present; resolve manually before confirming |
| `409` | `project_unavailable` | The project's working directory is not accessible |

### DELETE /api/projects/{id}/casting/proposals/{pid}

Rejects a proposal. No `.squad/` files are written or modified. Response `204 No Content`.

### GET /api/projects/{id}/team

Returns the current team roster and layout metadata.

Response `200 OK`:

```json
{
  "project_name": "my-project",
  "universe": "star-wars",
  "layout": "canonical",
  "migration_available": false,
  "members": [
    {
      "name": "Han Solo",
      "role_title": "Software Engineer",
      "charter_path": ".squad/HanSolo/charter.md",
      "status": "active",
      "default_model": "gpt-4o",
      "is_named": true,
      "charter_created_at": "2026-06-07T21:00:00+00:00",
      "charter_updated_at": "2026-06-07T21:05:00+00:00"
    }
  ]
}
```

| Field | Values | Notes |
| --- | --- | --- |
| `layout` | `"canonical"`, `"legacy"`, `"conflict"`, `"absent"` | `.squad/<Name>/` = canonical; `.squad/casting/<Name>/` = legacy; both present = conflict |
| `migration_available` | bool | `true` when a legacy layout exists and no canonical layout is present |
| `status` | `"active"`, `"retired"` | Member lifecycle state |

Returns `404 Not Found` when no team exists for the project.

### GET /api/projects/{id}/team/members/{name}/charter

Returns the charter for a team member as a JSON object.

Response `200 OK`:

```json
{
  "member_name": "Han Solo",
  "content": "# Han Solo\n\nYou are Han Solo, Software Engineer..."
}
```

Returns `404 Not Found` when the member does not exist or has no charter file.

### PUT /api/projects/{id}/team/members/{name}/charter

Replaces the charter for a team member.

Request:

```json
{
  "content": "# Han Solo\n\nUpdated charter content..."
}
```

Response `204 No Content` on success. Returns `404 Not Found` when the member does not exist.

### POST /api/projects/{id}/team/members

Adds a new member to the team. Creates the member's `.squad/` directory and an initial charter file generated from the specified role.

Request:

```json
{
  "role_id": "software-engineer",
  "custom_role_title": null,
  "model_id": null
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `role_id` | string | Yes | Role ID from `GET /api/catalog/roles` |
| `custom_role_title` | string | No | Override the role's default title for this member |
| `model_id` | string | No | Override the role's default model for this member |

Response `200 OK` — a `TeamMemberDto` (same shape as members in `GET /api/projects/{id}/team`).

### DELETE /api/projects/{id}/team/members/{name}

Retires a team member. Their `.squad/` directory and charter file are preserved; the member's status is set to `"retired"`.

Response `204 No Content`. Returns `404 Not Found` when the member does not exist.

### PATCH /api/projects/{id}/team/members/{name}

Re-roles an existing member, regenerating their charter for the new role.

Request:

```json
{
  "new_role_id": "product-manager",
  "custom_role_title": null
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `new_role_id` | string | Yes | New role ID from `GET /api/catalog/roles` |
| `custom_role_title` | string | No | Override the role's default title |

Response `200 OK` — the updated `TeamMemberDto`. Returns `404 Not Found` when the member does not exist.

### GET /api/projects/{id}/team/sync

Returns the pending uncommitted changes in the project's `.squad/` directory and a hash of the current change set.

Response `200 OK`:

```json
{
  "changes": [
    { "path": ".squad/HanSolo/charter.md", "kind": "modified" }
  ],
  "change_set_hash": "sha256:a1b2c3...",
  "nothing_to_sync": false
}
```

`changes` is an empty array and `nothing_to_sync` is `true` when there is nothing to commit. `change_set_hash` must be passed to `POST /api/projects/{id}/team/sync` to prevent stale commits.

### POST /api/projects/{id}/team/sync

Commits the pending `.squad/` changes to the project repository.

Request:

```json
{
  "expected_change_set_hash": "sha256:a1b2c3...",
  "message": "Update Han Solo charter"
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `expected_change_set_hash` | string | Yes | Hash from `GET /api/projects/{id}/team/sync`. The server rejects the commit if the change set has shifted since you fetched it. |
| `message` | string | No | Commit message. A default message is used when omitted. |

Returns `409 Conflict` with `error: "sync_state_changed"` when the change set hash does not match. Fetch a fresh hash from `GET /api/projects/{id}/team/sync` and retry.

Response `204 No Content` on success.

## Persistence

SQLite tables are created on startup with WAL enabled:

| Table | Purpose |
| --- | --- |
| `runs` | Run records with status, timing, submitting user, task, model source, model id, project id, and the final result text |
| `projects` | Project records with name, origin, working directory, default branch, owner, provider settings, and state |
| `github_tokens` | Per-user GitHub tokens stored by the OS credential store (not a SQLite table — managed by `OsCredentialStoreGitHubTokenStore`) |

The run's event stream is held in memory by `RunStreamStore` and is not persisted to SQLite. After a process restart, the granular event history is unavailable — only the final `result` text survives. Completed runs are persisted via the Copilot SDK session store (session ID = `scaffolder-run-{runId}`). The `GET /api/runs/{id}/history` endpoint replays persisted session events for terminal runs.

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
