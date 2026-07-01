# API reference

The Agentweaver backend is the single source of truth for run lifecycle, streaming, review, and merge. Every client is a thin layer over these endpoints.

- Base path: `/api`
- Authentication: bearer API key on authenticated API requests
- Event ordering: use `sequence`, not `timestamp`

## Authentication

Send the API key on API requests unless the endpoint is explicitly public (`/`, `/health`, `/auth/github/*`, or `/api/server/info`):

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

A request without a recognized key returns `401 Unauthorized`. A request for a run the caller does not own returns `403 Forbidden`. When no keys are configured, authenticated API requests are unauthorized.

Authorization is ownership-based after authentication. Project, team, run, backlog, workflow, workspace, and memory endpoints load the relevant resource and require the authenticated caller to own it (`caller.Owns(...)` in endpoint code) before returning or mutating it. Agentweaver has no built-in superuser role derived from a GitHub username: a login named `admin` is treated like any other caller and does not bypass ownership checks.

## Endpoints

### Runs

Run endpoints are owner-scoped. The authenticated caller must own the run being read, streamed, reviewed, retried, archived, merged, steered, or used for sandbox preview; there is no username-based administrative override.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/` | Health banner (`Agentweaver API`) |
| `POST` | `/api/runs` | Submit a task and start a run |
| `GET` | `/api/runs/{id}` | Get current run state |
| `POST` | `/api/runs/{id}/archive` | Archive a run |
| `DELETE` | `/api/runs/{id}` | Delete a run record |
| `GET` | `/api/runs/{id}/stream` | Stream ordered run events over SSE |
| `GET` | `/api/runs/{id}/events` | Return persisted run events |
| `POST` | `/api/runs/{id}/review` | Record an approve or decline decision |
| `POST` | `/api/runs/{id}/shell-approvals` | Approve a pending destructive shell command |
| `POST` | `/api/runs/{id}/shell-denials` | Deny a pending destructive shell command |
| `GET` | `/api/runs/{id}/history` | Replay persisted session events for terminal runs |
| `GET` | `/api/runs/{id}/graph` | Get the workflow graph descriptor for rendering the run topology |
| `POST` | `/api/runs/{id}/commit` | Commit worktree changes and merge into originating branch |
| `POST` | `/api/runs/{id}/request-changes` | Request a revision cycle: agent rewrites in place |
| `POST` | `/api/runs/{id}/retry` | Retry a failed run as a new linked run |
| `GET` | `/api/runs/{id}/workspace` | List workspace files with change status and line counts |
| `GET` | `/api/runs/{id}/files` | List changed files (flat, with filter) |
| `GET` | `/api/runs/{id}/files/{**path}` | Get diff or content for a specific file |
| `POST` | `/api/runs/{id}/tool-approvals` | Approve a pending tool call |
| `POST` | `/api/runs/{id}/tool-denials` | Deny a pending tool call |
| `POST` | `/api/runs/{id}/questions/{requestId}/answer` | Answer a pending `ask_question` request |
| `POST` | `/api/runs/{id}/auto-approve` | Toggle the per-run auto-approve-tools option |
| `POST` | `/api/runs/{id}/autopilot` | Toggle the coordinator Autopilot option |
| `POST` | `/api/runs/{runId}/sandbox/port-forward` | Start a sandbox pod port-forward |
| `GET` | `/api/runs/{runId}/sandbox/port-forward` | List sandbox port-forwards for a run |
| `DELETE` | `/api/runs/{runId}/sandbox/port-forward/{sessionId}` | Stop a sandbox port-forward |

### Projects

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects` | Create a project (blank or from GitHub) |
| `GET` | `/api/server/info` | Get server metadata |
| `GET` | `/api/projects` | List all projects |
| `GET` | `/api/projects/{id}` | Get a project by id |
| `PATCH` | `/api/projects/{id}` | Rename a project |
| `PUT` | `/api/projects/{id}/provider-settings` | Update provider and model defaults |
| `POST` | `/api/projects/{id}/relink` | Relink a project to a moved directory |
| `DELETE` | `/api/projects/{id}` | Delete a project (record only; cancels active runs) |
| `GET` | `/api/projects/{id}/runs` | List runs for a project |
| `GET` | `/api/projects/{id}/runs/{workflowRunId}` | Get a project workflow-run summary |
| `POST` | `/api/projects/{id}/runs` | Start a run within a project |
| `POST` | `/api/projects/{id}/orchestrations` | Start a coordinator orchestration |

Run summary objects returned by `GET /api/projects/{id}/runs` include a `result` field (`"no_changes"` or `null`). When `result` is `"no_changes"`, the agent found no file changes to commit; the review and merge gates are skipped. Each summary also includes `coordinator_status`: for a coordinator run (`agent_name: "Coordinator"`, no parent) this is the current work-plan orchestration status (`dispatching`, `awaiting_assembly`, `assembling`, `in_review`, `complete`, `assembly_blocked`, `assembly_failed`, `assembly_declined`); it is `null` for normal runs. A companion `coordinator_status_reason` (the coordinator run's `result`, scoped to coordinator rows) carries the human-readable terminal/failure detail so the UI can render "Failed: &lt;reason&gt;". Children are excluded from this list. The UI should render `coordinator_status` (plus `coordinator_status_reason`) for coordinator rows so a long-running assembly does not show as a bare `in_progress` and a terminal failure does not show as an unexplained `failed`.

### Memory

Memory is scoped to projects. Decisions and memories feed the `MemoryContextCompiler`, which assembles a hierarchical context block injected into every agent run (boundaries → core context → learnings → session). Export writes to `.squad/decisions.md`, `.squad/agents/{name}/history.md`, `.agentweaver/context/boundaries.md`, and `.agentweaver/context/patterns.md`.

#### Decision Inbox

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/decisions/inbox` | Submit a decision or learning to the inbox |
| `GET` | `/api/projects/{id}/decisions/inbox` | List inbox entries (`?agent=`, `?type=`, `?status=`) |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/merge` | Merge a pending entry into decisions |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/promote` | Alias for merge/promote |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/reject` | Reject a pending entry |

#### Decisions

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/decisions` | Create a decision directly |
| `GET` | `/api/projects/{id}/decisions` | List decisions (`?type=`, `?agent=`) |
| `GET` | `/api/projects/{id}/decisions/{decisionId}` | Get a single decision |
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
| `GET` | `/api/projects/{id}/sessions` | List sessions |
| `PATCH` | `/api/projects/{id}/sessions/{sessionId}` | Update a specific session |

#### Export / Import

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/projects/{id}/memory/export` | Export DB memory → `.squad/` + `.agentweaver/context/` |
| `POST` | `/api/projects/{id}/memory/import` | Import `.squad/decisions/inbox/*.md` → DB |

### GitHub authentication

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/auth/github/authorize` | Begin the GitHub OAuth redirect flow |
| `GET` | `/auth/github/callback` | Receive the GitHub OAuth callback |
| `POST` | `/api/auth/github/device` | Start the GitHub device authorization flow |
| `POST` | `/api/auth/github/poll` | Poll the device flow for completion |
| `GET` | `/api/auth/github` | Get current GitHub authentication status |
| `GET` | `/api/github/repos` | List repositories for the signed-in GitHub user |
| `POST` | `/api/auth/github/sign-out` | Sign out and delete the stored token |

### Team casting

Team and casting endpoints are project-scoped and use the project owner check before exposing or changing rosters, charters, proposals, or sync state. A caller who does not own the project cannot manage that project's team.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/casting/templates` | List available scenario groupings (team templates) |
| `GET` | `/api/projects/{id}/casting/universes` | List allowed universe names |
| `GET` | `/api/catalog/roles` | List all available role definitions |
| `POST` | `/api/projects/{id}/casting/proposals` | Create a casting proposal |
| `GET` | `/api/projects/{id}/casting/proposals` | List active proposals for a project |
| `GET` | `/api/projects/{id}/casting/proposals/{proposalId}` | Get a proposal |
| `PATCH` | `/api/projects/{id}/casting/proposals/{proposalId}` | Amend a proposal |
| `POST` | `/api/projects/{id}/casting/proposals/{proposalId}/confirm` | Confirm a proposal and create the team |
| `DELETE` | `/api/projects/{id}/casting/proposals/{proposalId}` | Reject a proposal |
| `GET` | `/api/projects/{id}/team` | Get team roster and layout metadata |
| `GET` | `/api/projects/{id}/team/members/{name}/charter` | Get a member's charter |
| `PUT` | `/api/projects/{id}/team/members/{name}/charter` | Replace a member's charter |
| `GET` | `/api/projects/{id}/team/members/{name}/history` | Get agent interaction history |
| `POST` | `/api/projects/{id}/team/members` | Add a team member |
| `DELETE` | `/api/projects/{id}/team/members/{name}` | Retire a team member |
| `PATCH` | `/api/projects/{id}/team/members/{name}` | Re-role a team member |
| `GET` | `/api/projects/{projectId}/team/sync` | Get pending .squad/ changes and change set hash |
| `POST` | `/api/projects/{projectId}/team/sync` | Commit pending .squad/ changes |

Team member objects include `is_built_in: true` for Scribe, Ralph, and Rai (case-insensitive). Built-in agents cannot be removed, re-roled, or directly run. Attempting to start a run with a built-in agent name returns `400 Bad Request`.

### Blueprints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/blueprints` | List predefined blueprints |
| `POST` | `/api/blueprints/generate` | Generate a blueprint from a description |
| `POST` | `/api/blueprints/validate` | Validate an inline blueprint |

### Backlog, board, and workflow setup

Backlog, board, review-policy, and workflow endpoints are project-scoped and require ownership of the containing project. Backlog tasks do not introduce separate cross-user privileges; callers manage only tasks in projects they own.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/projects/{id}/workspace/files` | List project workspace files for decomposition |
| `POST` | `/api/projects/{id}/backlog/decompose` | Decompose workspace files into backlog tasks |
| `POST` | `/api/projects/{projectId}/backlog/tasks` | Create a backlog task |
| `PATCH` | `/api/projects/{projectId}/backlog/tasks/{taskId}` | Edit a backlog task title/description |
| `DELETE` | `/api/projects/{projectId}/backlog/tasks/{taskId}` | Delete a backlog task |
| `POST` | `/api/projects/{projectId}/backlog/tasks/{taskId}/ready` | Move a task to ready |
| `POST` | `/api/projects/{projectId}/backlog/ready-all` | Move all eligible tasks to ready |
| `POST` | `/api/projects/{projectId}/backlog/tasks/{taskId}/backlog` | Move a task back to backlog |
| `POST` | `/api/projects/{projectId}/backlog/tasks/{taskId}/reorder` | Reorder a backlog task |
| `POST` | `/api/projects/{projectId}/backlog/tasks/{taskId}/archive` | Archive a backlog task |
| `GET` | `/api/projects/{projectId}/board` | Get the board state |
| `GET` | `/api/projects/{projectId}/workflow-stages` | Get workflow stages |
| `GET` | `/api/projects/{projectId}/backlog/settings` | Get backlog pickup settings |
| `PUT` | `/api/projects/{projectId}/backlog/settings` | Update backlog pickup settings |
| `GET` | `/api/projects/{projectId}/review-policies` | List review policies |
| `POST` | `/api/projects/{projectId}/review-policies/sync` | Reload review policies from disk |
| `GET` | `/api/projects/{projectId}/review-policies/{policyName}` | Get a review policy |
| `PUT` | `/api/projects/{projectId}/review-policies/active` | Set the active review policy |
| `GET` | `/api/projects/{projectId}/workflows` | List workflow definitions |
| `POST` | `/api/projects/{projectId}/workflows/sync` | Reload workflow definitions from disk |
| `GET` | `/api/projects/{projectId}/workflows/{workflowId}` | Get a workflow definition |
| `PUT` | `/api/projects/{projectId}/workflows/default` | Set the default workflow |
| `PUT` | `/api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override` | Set a task workflow override |
| `GET` | `/api/projects/{projectId}/workflows/{workflowId}/graph` | Get a workflow graph |
| `GET` | `/api/projects/{projectId}/workflows/{workflowId}/yaml` | Get workflow YAML |
| `PUT` | `/api/projects/{projectId}/workflows/{workflowId}` | Replace a workflow definition |
| `POST` | `/api/projects/{projectId}/workflows/generate` | Generate a workflow definition |

### Workspace, diagnostics, and metrics

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Public liveness probe |
| `GET` | `/api/health` | API liveness probe |
| `GET` | `/api/diagnostics` | Get API diagnostics (SQLite/disk/workflow/heartbeat) |
| `GET` | `/api/diagnostics/cluster` | Get cluster diagnostics (pods, quota, component health, pending runs) |
| `GET` | `/api/diagnostics/heartbeat` | Get diagnostics heartbeat |
| `GET` | `/api/projects/{id}/diagnostics` | Get project diagnostics |
| `GET` | `/api/projects/{id}/workspace/refs` | List workspace refs |
| `GET` | `/api/projects/{id}/workspace` | List project workspace files |
| `GET` | `/api/projects/{id}/workspace/files/{**path}` | Read a project workspace file |
| `GET` | `/api/projects/{id}/dashboard` | Get project dashboard metrics (includes `token_usage` field) |
| `GET` | `/api/overview` | Get global overview metrics (includes `token_usage` field for admins) |
| `GET` | `/api/runs/{id}/usage` | Get token usage summary for a run |
| `GET` | `/api/workflow-runs/{id}/usage` | Get token usage summary for a workflow-run envelope |
| `GET` | `/api/projects/{id}/usage` | Get project token usage, time-ranged (default: last 30 days) |
| `GET` | `/api/usage` | Get app-wide token usage, admin only (default: last 30 days) |

### GET /api/diagnostics/cluster

Returns a `ClusterDiagnosticsDto` with the current state of the Kubernetes cluster as seen by the Agentweaver API. Requires authentication. Returns `404 Not Found` when cluster diagnostics are not available (e.g. non-AKS deployment).

Six component health checks run **concurrently** with a 5-second individual timeout each:

| Check name | What it tests |
| --- | --- |
| `postgresql` | Postgres connectivity |
| `github_installation_token` | GitHub token-store validity for the configured scope |
| `key_vault` | Azure Key Vault reachability and required `mcp-oauth-signing-key` lookup. `critical: secret 'mcp-oauth-signing-key' not found` means `scripts/aks/16-provision-oauth-signing-key.sh` was skipped. |
| `agent_pod_quota` | CPU headroom ≥ 2 CPU in the sandbox namespace |
| `warm_pool` | Warm-pool agent-sandbox availability |
| `kubernetes_api` | Kubernetes API server reachability |

Response `200 OK` — a `ClusterDiagnosticsDto`:

```json
{
  "component_health": [
    { "name": "postgresql", "status": "pass", "detail": null, "duration_ms": 12 },
    { "name": "agent_pod_quota", "status": "warn", "detail": "CPU headroom: 1.2 cores", "duration_ms": 45 }
  ],
  "namespace_quota": {
    "cpu_used": 3.8,
    "cpu_total": 5.0,
    "memory_used_gi": 6.4,
    "memory_total_gi": 10.0
  },
  "active_agent_pods": [
    { "pod_name": "agent-host-abc123", "run_id": "f36800fd-...", "node": "katapool-vm-1", "started_at": "2026-06-27T17:55:00Z" }
  ],
  "orphaned_agent_pods": [],
  "pending_capacity_runs": [
    { "coordinator_run_id": "coord-...", "subtask_id": 7, "pending_since": "2026-06-27T17:58:30Z", "retry_count": 3 }
  ]
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `component_health` | `ComponentHealthDto[]` | One entry per check; `status` is `pass`, `warn`, or `fail`. |
| `namespace_quota` | object | Current CPU and memory usage vs. namespace limits. |
| `active_agent_pods` | `AgentPodInfoDto[]` | Pods currently running with a matching active run. |
| `orphaned_agent_pods` | `AgentPodInfoDto[]` | Pods running with no matching active run (candidates for next reaper sweep). |
| `pending_capacity_runs` | `PendingCapacityRunDto[]` | Subtasks waiting for CPU capacity to become available. |

See [Cluster diagnostics reference](./cluster-diagnostics.md) for the full DTO schema and field descriptions.

### GET /api/diagnostics/heartbeat — automation_name

The heartbeat tick records returned by `GET /api/diagnostics/heartbeat` include an `automation_name` field on each `TickRecordDto`:

```json
{
  "tick_records": [
    {
      "automation_name": "Coordinator Heartbeat",
      "acted_count": 2,
      "error_count": 0,
      "duration_ms": 340,
      "recorded_at": "2026-06-27T18:00:00Z"
    }
  ]
}
```

The Heartbeat page **Recent Activity** table shows this as the first column (**Automation**). Possible values are `"Coordinator Heartbeat"` and `"Checkpoint GC"`.

### GET /

Returns the plain text banner `Agentweaver API`.

### POST /api/runs

Submits a task and starts a run. The API creates a dedicated branch (`agentweaver/{runId}`), provisions a git worktree from the originating branch, and starts the agent loop in the background.

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

An optional `"auto_approve_tools": true` may be set to launch the run with the auto-approve-tools option ON (see `POST /api/runs/{id}/auto-approve`). It defaults to `false`.

`repository_path` must be an absolute local filesystem path. The server canonicalizes it with `Path.GetFullPath` before storing it on the run record. UNC paths (`\\server\share`, `//server/share`), device paths (`\\?\`, `\\.\`), drive-relative paths (`C:foo`), relative paths, and NTFS Alternate Data Streams are rejected with `400`.

When `Runs:AllowedRepositoryRoots` is configured (non-empty string array), the server resolves the canonical path through symlinks and junctions and verifies that the resolved location is inside one of the allowed roots. Paths outside the allowlist return `400`. By default no allowlist is configured and any valid local absolute path is accepted. Shared, exposed, or multi-tenant deployments MUST set an allowlist to prevent users from targeting arbitrary repositories on the server filesystem.

Response `202 Accepted`:

```json
{ "run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6", "workflow_run_id": "f36800fd-f2f8-418c-958e-aae3e4921ba6", "status": "in_progress" }
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

For a **coordinator** run (`agent_name: "Coordinator"`, no parent), the response also carries `coordinator_status`: the current work-plan orchestration status (`dispatching`, `awaiting_assembly`, `assembling`, `in_review`, `complete`, `assembly_blocked`, `assembly_failed`, `assembly_declined`). It is `null` for normal runs and for coordinator runs that have no work plan yet. Because a coordinator run stays `in_progress` while it dispatches children and runs collective assembly, `coordinator_status` is what the UI should render (for example "Awaiting assembly" or "Failed: &lt;result&gt;") instead of the bare `status`. On a terminal assembly failure the `result` — also surfaced as `coordinator_status_reason` on this response (scoped to coordinator runs) — carries the human-readable reason (for example `assembly_blocked: <reason>`, `assembly_merge_failed: <reason>`, `assembly_error: <message>`).

The response also carries `auto_approve_tools` and `autopilot` (booleans) reflecting the current per-run option state (launch value plus any live toggle). Both are `false` unless explicitly enabled. The frontend uses these to render the toggle controls; see `POST /api/runs/{id}/auto-approve` and `POST /api/runs/{id}/autopilot`.

### POST /api/runs/{id}/archive

Archives a run for the owner. Response `200 OK`:

```json
{ "run_id": "f36800fd-...", "archived_at": "2026-06-07T21:20:00+00:00" }
```

### DELETE /api/runs/{id}

Deletes a run record. Active runs are abandoned and their worktree cleanup is best effort before deletion. Response `204 No Content`.

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

### POST /api/runs/{id}/shell-approvals

Approves a pending shell command. Use the `commandHash` from the `shell.approval_required` event as `command_hash`.

Request:

```json
{ "command_hash": "sha256:..." }
```

Response `200 OK` `{ "run_id", "command_hash", "approved": true }`.

### POST /api/runs/{id}/shell-denials

Denies a pending shell command.

Request:

```json
{ "command_hash": "sha256:..." }
```

Response `200 OK` `{ "run_id", "command_hash", "denied": true }`.

### GET /api/runs/{id}/events

Returns persisted run events ordered by `sequence`. Each item has `sequence`, `type`, and `payload`.

### GET /api/runs/{id}/history

Replays persisted Copilot SDK session events for a terminal run. The session is identified by `agentweaver-run-{runId}`. Returns a JSON array of run events in stream order. Only available for terminal runs. Returns `404` if the run is not terminal or the session is not found.

### GET /api/runs/{id}/graph

Returns the workflow graph descriptor for the run, describing the node/edge topology so a client can render the live workflow without hardcoding it. The descriptor is built from the same code that wires the MAF workflow (no runtime reflection). Owner-scoped Bearer auth. Coordinator runs (`parent_run_id == null`, driven by the built-in Coordinator agent, with a persisted work plan) return the `coordinator` variant (see below); child runs (`parent_run_id != null`) return the `child` variant; all others return the `full` variant.

Response `200 OK` — a `GraphDescriptor`:

```json
{
  "graph_id": "agentweaver-workflow-full",
  "variant": "full",
  "start_node_id": "agent",
  "nodes": [
    { "id": "agent", "label": "Agent", "role": "agent", "kind": "live", "node_type": "agent", "child_graph_ref": null }
  ],
  "edges": [
    { "from": "agent", "to": "rai", "cardinality": "direct", "loopback": false }
  ]
}
```

- `variant`: `"full"` | `"child"` | `"coordinator"`.
- `nodes[].id`: the logical node id (matches the step key in `workflow.step` events). `kind`: `"live"` | `"planned"`. `child_graph_ref`: optional reference to a nested graph.
- `nodes[].node_type`: self-declared category that drives the frontend's rendered shape/size — one of `"agent"` (an AI agent turn), `"action"` (a deterministic system op), `"gate"` (a human-in-the-loop decision/approval), `"terminal"` (a workflow endpoint/checkpoint), or `"subtask"` (a coordinator fan-out child reference). Required on every node.
- `edges[].cardinality`: `"direct"` | `"fanout"` | `"fanin"`. `loopback`: `true` when the edge targets an ancestor (a revision cycle back-edge).

#### Coordinator variant

When the run is a coordinator run, the descriptor is built from its work plan (`graph_id` = `coordinator:{coordinatorRunId}`, `start_node_id` = `coordinator`) so the same generic renderer can draw the coordinator, its fan-out subtask children, and the PLANNED Phase 3 collective-assembly stage. It is shape-only — runtime status is NOT baked in (project it from the `subtask.*` / `coordinator.topology` streams).

- Node `coordinator` (`node_type: "agent"`, `role: "coordinator"`, `kind: "live"`).
- One node per subtask, id `plan:subtask-{id}` (`node_type: "subtask"`, `role: "subtask"`, `kind: "live"`). Subtask nodes carry rich display fields as OPTIONAL snake_case properties (omitted when null): `agent`, `model`, `phase`, `isolation`, `child_run_id`. Once the subtask's child run is dispatched, `child_graph_ref` is `run:{childRunId}` so the client can expand the child's own graph via `GET /api/runs/{childRunId}/graph`; it is `null` until dispatched.
- PLANNED collective-assembly nodes (`kind: "planned"`): `planned:assembly-rai` (`node_type: "agent"`, `role: "rai"`), `planned:assembly-review` (`node_type: "gate"`, `role: "review"`), `planned:assembly-merge` (`node_type: "action"`, `role: "merge"`), `planned:assembly-scribe` (`node_type: "agent"`, `role: "scribe"`).
- Edges: `coordinator` → each root subtask; dependency edges `plan:subtask-{dependsOn}` → `plan:subtask-{dependent}`; each terminal (leaf) subtask → `planned:assembly-rai`; then the assembly chain `assembly-rai` → `assembly-review` → `assembly-merge` → `assembly-scribe`. Two loopback back-edges (`loopback: true`) close the cycle: `planned:assembly-rai` → `coordinator` and `planned:assembly-review` → `coordinator`, reflecting that an RAI flag or a human-review request-changes re-dispatches affected subtasks through the coordinator. All forward edges are `loopback: false`. `cardinality` is `fanout`/`fanin` by forward (non-loopback) degree; loopback edges are always `direct` and are excluded from the degree counts so they do not distort fan-out/fan-in.

```json
{
  "graph_id": "coordinator:run_abc",
  "variant": "coordinator",
  "start_node_id": "coordinator",
  "nodes": [
    { "id": "coordinator", "label": "Coordinator", "role": "coordinator", "kind": "live", "node_type": "agent", "child_graph_ref": null },
    { "id": "plan:subtask-1", "label": "Build API", "role": "subtask", "kind": "live", "node_type": "subtask", "child_graph_ref": "run:run_child1", "agent": "morpheus", "model": "gpt-5.3-codex", "phase": "execution", "isolation": "worktree", "child_run_id": "run_child1" }
  ],
  "edges": [
    { "from": "coordinator", "to": "plan:subtask-1", "cardinality": "fanout", "loopback": false }
  ]
}
```

The same descriptor is emitted once at run start as a `run.workflow_graph` event on the stream (see [events.md](events.md)).

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

### POST /api/runs/{id}/retry

Retries a failed or merge-failed run as a new linked run. The source run is not mutated; child runs are retried through their coordinator parent.

Response `201 Created`:

```json
{ "run_id": "new-run-id", "retried_from": "old-run-id", "status": "in_progress" }
```

### GET /api/runs/{id}/workspace

Returns a tree of all files in the run's worktree (folders + files). Files include `path`, `is_folder`, `status` (added/modified/deleted, null for unchanged), `added_lines`, `removed_lines`. Returns `404` for terminal runs whose worktrees have been removed (failed/merged/declined/merge_failed). Returns empty array for `pending`. Returns `409` while the worktree does not exist for an active run.

### GET /api/runs/{id}/files

Flat list of changed files. Query param `filter`: `all` (committed + uncommitted), `committed`, `uncommitted`, `last-commit`. Returns `409` for non-terminal runs with no worktree.

### GET /api/runs/{id}/files/{**path}

Returns diff and content for a specific file. Response includes `path`, `status`, `diff`, `content`, `is_binary`.

### POST /api/runs/{id}/tool-approvals

Approves a pending tool call.

Request:

```json
{ "request_id": "string", "scope": "once" | "run" | "always" | "tool" }
```

Scope values: `once` = this call only; `run` = all calls to the same tool+url this run; `always` = all calls this server session; `tool` = all calls to this tool regardless of url.

Response `200 OK` `{ "run_id", "request_id", "approved": true }`.

Errors: `400` invalid run id / missing `request_id`; `404` run not found; `403` caller is not the run owner; `409` no pending approval for this `request_id` or run is not active.

### POST /api/runs/{id}/tool-denials

Denies a pending tool call.

Request:

```json
{ "request_id": "string" }
```

Response `200 OK` `{ "run_id", "request_id", "denied": true }`.

Errors: `400` invalid run id / missing `request_id`; `404` run not found; `403` caller is not the run owner; `409` no pending denial for this `request_id` or run is not active.

### POST /api/runs/{id}/questions/{requestId}/answer

Answers a pending `ask_question` request, resuming the agent that called the `ask_question` tool. The `requestId` is the value carried by the `agent.question_asked` event. For a coordinator child run, answer against the CHILD run id (carried by `coordinator.child_question`).

Request:

```json
{ "answer": "string" }
```

Response: `200 OK` `{ "run_id", "request_id", "answered": true }`.

Errors: `400` invalid run id / missing `answer`; `404` run not found; `409` no pending question for this `request_id` (already answered, timed out, or never asked); `403` caller is not the run owner. The run must be `InProgress`.

### POST /api/runs/{id}/auto-approve

Toggles the per-run **auto-approve-tools** option. When enabled, an allow-with-approval tool request (e.g. `web_fetch`) is auto-granted at the human-in-the-loop gate instead of stalling for an operator. Every auto-grant is logged on the timeline as a `tool.auto_approved` event. This NEVER overrides a policy deny: dangerous tools are rejected upstream by sandbox governance before the gate is reached. The flag is settable at launch (`auto_approve_tools` on `POST /api/runs`; `autoApproveTools` on `POST /api/projects/{id}/orchestrations`) and cascades from a coordinator run to its dispatched children. Defaults to OFF.

Request:

```json
{ "enabled": true }
```

Response: `200 OK` `{ "run_id", "auto_approve_tools": true }`.

Errors: `400` invalid run id; `404` run not found; `403` caller is not the run owner; `409` run is not active (`InProgress`).

### POST /api/runs/{id}/autopilot

Toggles the coordinator **Autopilot** option. When enabled, CLARIFYING QUESTIONS ONLY (the coordinator's own and those bubbled by child workers as `coordinator.child_question`) are auto-answered by the coordinator model from the outcome spec + subtask context, then resolved on the child's question gate. Each auto-answer is logged as `coordinator.autopilot_answered`, and the normal `agent.question_answered` resolution still surfaces on the child stream. Autopilot does NOT auto-grant tool approvals/permissions (that is the separate auto-approve-tools opt-in). Settable at launch (`autopilot` on `POST /api/projects/{id}/orchestrations`) and cascades to children. Defaults to OFF.

Request:

```json
{ "enabled": true }
```

Response: `200 OK` `{ "run_id", "autopilot": true }`.

Errors: `400` invalid run id; `404` run not found; `403` caller is not the run owner; `409` run is not active (`InProgress`).

## Sandbox port-forward endpoints

These endpoints back the Kubernetes sandbox preview feature by running `kubectl port-forward` to the sandbox pod for a run. They are owner-scoped like other run endpoints.

### POST /api/runs/{runId}/sandbox/port-forward

Starts a port-forward session from a random local port to the sandbox pod's target port.

Request:

```json
{ "target_port": 3000 }
```

Response `200 OK`:

```json
{
  "session_id": "pf-abc123",
  "local_port": 54321,
  "target_port": 3000,
  "pod_name": "agentweaver-agent-host-...",
  "started_at": "2026-06-07T21:00:00+00:00"
}
```

`target_port` must be between 1 and 65535. Start failures return `409 Conflict` with an `error` message.

### GET /api/runs/{runId}/sandbox/port-forward

Lists active port-forward sessions for the run. Response `200 OK` is an array of `{ session_id, local_port, target_port, pod_name, started_at }`.

### DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}

Stops an active port-forward session. Response `200 OK`:

```json
{ "session_id": "pf-abc123", "stopped": true }
```

Returns `404 Not Found` when the run or port-forward session does not exist.

## Sandbox policy endpoints

These endpoints read and write the per-project sandbox execution policy stored at `.agentweaver/settings.yml` in the project repository root. Sandbox policies control whether shell execution is enabled, which commands require human approval, and output handling options. See [sandbox-setup.md](sandbox-setup.md) for setup and [deep-dive/sandboxed-execution.md](../deep-dive/sandboxed-execution.md) for the full design.

### GET /api/sandbox-policy

Returns the sandbox policy for the given repository path by reading `{repository_path}/.agentweaver/settings.yml`. If the file does not exist, returns the default policy.

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

Creates or replaces the sandbox policy for a repository path by writing `{repository_path}/.agentweaver/settings.yml`. The entire policy is replaced on each PUT; there is no partial-update merge. After a PUT, the operator should commit the updated file to the project repository to record the change in version history.

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

## Blueprint endpoints

Blueprint endpoints are global and authenticated. A blueprint response includes both the legacy `workflow` field and the full `workflows` array. Generated or inline blueprints may include `bespoke_roles`; each bespoke role id must also appear in `roster`.

Blueprint shape:

```json
{
  "id": "web-app",
  "name": "Web App",
  "description": "Frontend + API application",
  "roster": ["product-manager", "bespoke-domain-expert"],
  "workflow": "default",
  "workflows": ["default"],
  "review_policy": "default",
  "sandbox_profile": "default",
  "bespoke_roles": [
    {
      "id": "bespoke-domain-expert",
      "title": "Domain Expert",
      "charter": "Inline charter text used when no catalog role fits."
    }
  ]
}
```

### GET /api/blueprints

Lists predefined blueprints.

Response `200 OK`:

```json
{ "blueprints": [ { "...": "BlueprintDto" } ] }
```

### POST /api/blueprints/generate

Generates a single blueprint from a free-text description.

Request:

```json
{ "description": "Build a travel-planning assistant" }
```

Response `200 OK`:

```json
{
  "blueprint": { "...": "BlueprintDto" },
  "generated_workflow_yaml": null
}
```

`generated_workflow_yaml` is present when no suitable library workflow exists and a custom workflow was generated. Validation failures return `422 Unprocessable Entity` with `error: "blueprint_generation_failed"` and `details`.

### POST /api/blueprints/validate

Validates a blueprint shape, workflow/review policy references, sandbox profile, and roster roles. Roster entries must be catalog role ids or ids declared in `bespoke_roles`.

Request:

```json
{ "blueprint": { "...": "BlueprintDto" } }
```

Response `200 OK`:

```json
{ "valid": true, "errors": [] }
```

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

All project endpoints are caller-owned unless explicitly documented as public metadata. Creating a project records the authenticated caller as `owner`; listing returns only that caller's projects; project-scoped mutation and child-resource endpoints require that same ownership. Non-owned resources return `403 Forbidden` or `404 Not Found` depending on the endpoint's existence-leak behavior.

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
  "default_model_microsoft_foundry": null,
  "blueprint_id": null,
  "blueprint": null,
  "generated_workflow_yaml": null
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
| `blueprint_id` | string | No | Predefined blueprint id from `GET /api/blueprints`. Mutually exclusive with `blueprint`. |
| `blueprint` | object | No | Inline `BlueprintDto`, including optional `bespoke_roles`. Mutually exclusive with `blueprint_id`. |
| `generated_workflow_yaml` | string | No | Custom workflow YAML returned by `POST /api/blueprints/generate`; materialized before applying the blueprint. |

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
  "source_blueprint_id": null,
  "source_blueprint_type": null,
  "created_at": "2026-06-07T21:00:00+00:00",
  "updated_at": "2026-06-07T21:00:00+00:00"
}
```

`available` is `true` when the working directory exists on the server filesystem. `state` is `"active"` or `"deleting"`.

Validation failures return `400 Bad Request`.

### GET /api/projects

Returns all projects owned by the authenticated user. Each entry uses the same shape as the `POST /api/projects` response.

### GET /api/server/info

Returns public server metadata. Response `200 OK`:

```json
{ "data_directory": "C:/Users/name/AppData/Local/Agentweaver" }
```

### GET /api/projects/{id}

Returns a single project owned by the caller. Returns `404 Not Found` when no project exists for the given id or the caller does not own it.

### PATCH /api/projects/{id}

Renames a caller-owned project.

Request:

```json
{ "name": "new-name" }
```

Response `204 No Content` on success. `400` when `name` is missing. `404` when the project does not exist or is not owned by the caller.

### PUT /api/projects/{id}/provider-settings

Updates the provider and model defaults for a caller-owned project.

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

Updates the working directory path for a caller-owned project. Use this after moving a repository to a new location.

Request:

```json
{ "working_directory": "D:/new-location/my-project" }
```

Response `204 No Content` on success. `400` when the new path is missing or not a valid git repository.

### DELETE /api/projects/{id}

Deletes a caller-owned project record. Does not touch the working directory or git history. Active runs for the project are cancelled; each cancelled run emits a `run.cancelled` event on its stream.

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
    "workflow_run_id": "workflow-...",
    "execution_id": "f36800fd-...",
    "status": "merged",
    "model_id": null,
    "task": "add license headers",
    "agent_name": "Aria",
    "reviewed_by": "local-developer",
    "started_at": "2026-06-07T21:09:45+00:00",
    "ended_at": "2026-06-07T21:10:12+00:00",
    "result": null,
    "coordinator_status": null,
    "coordinator_status_reason": null,
    "archived_at": null
  }
]
```

### GET /api/projects/{id}/runs/{workflowRunId}

Returns one project workflow-run summary by `workflow_run_id`. The response shape matches entries from `GET /api/projects/{id}/runs`. Returns `404 Not Found` when the run does not exist or belongs to another project.

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
{ "run_id": "f36800fd-...", "workflow_run_id": "workflow-...", "status": "pending" }
```

`409 Conflict` with `error: "project_deleting"` when the project is being deleted.
`409 Conflict` with `error: "workspace_unavailable"` when the working directory is not accessible. Use `POST /api/projects/{id}/relink` to reconnect the project to its new location.

## Coordinator endpoints

The Coordinator agent drafts a confirmable outcome spec for a goal, then suspends at a confirmation gate. These endpoints are a thin HTTP layer over `CoordinatorRunService`; all orchestration lives in the service. A coordinator run is an ordinary run (`agent_name: "Coordinator"`, no parent), so its events stream from `GET /api/runs/{id}/stream` and it is owner-scoped like any other run.

### POST /api/projects/{id}/orchestrations

Starts a coordinator run for the project. The project's working directory, default branch, and the authenticated caller are used as the run's repository path, originating branch, and submitting user. The provider is fixed to GitHub Copilot.

Request:

```json
{
  "goal": "Make the onboarding flow resumable across sessions",
  "modelId": null
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `goal` | string | Yes | The outcome the coordinator should draft a spec for. |
| `modelId` | string | No | Model override. Falls back to the project's GitHub Copilot default, then the role default. |
| `autoApproveTools` | bool | No | Launch with auto-approve-tools ON for the coordinator and its children. Defaults to `false`. |
| `autopilot` | bool | No | Launch with Autopilot ON (auto-answer clarifying questions only). Cascades to children. Defaults to `false`. |

Response `201 Created` (with `Location: /api/runs/{runId}`):

```json
{ "runId": "f36800fd-..." }
```

`400 Bad Request` when `id` is not a valid project id or `goal` is missing.
`404 Not Found` when the project does not exist.
`409 Conflict` with `error: "project_deleting"` when the project is being deleted.
`409 Conflict` with `error: "workspace_unavailable"` when the working directory is not accessible.

### GET /api/runs/{id}/outcome-spec

Returns the current persisted outcome spec for a coordinator run. Owner-scoped.

Response `200 OK`:

```json
{
  "goal": "Make the onboarding flow resumable across sessions",
  "desiredOutcome": "Users can leave and resume onboarding without losing progress",
  "scope": "Onboarding wizard, session persistence",
  "assumptions": "Existing session store can hold partial onboarding state",
  "clarifyingQuestions": "Should resumption work across devices?",
  "status": "awaiting_confirmation",
  "confirmedBy": null
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `goal` | string | The submitted goal. |
| `desiredOutcome` | string | The drafted desired outcome. |
| `scope` | string | Drafted scope. |
| `assumptions` | string | Drafted assumptions. |
| `clarifyingQuestions` | string | Omitted when none were drafted. |
| `status` | string | `drafting`, `awaiting_confirmation`, `confirmed`, or `declined`. |
| `confirmedBy` | string | Set once confirmed; omitted otherwise. |

`400 Bad Request` when `id` is not a valid run id.
`403 Forbidden` when the caller does not own the run.
`404 Not Found` when the run or its outcome spec does not exist.

### POST /api/runs/{id}/outcome-spec/confirm

Confirms the drafted outcome spec, resuming the suspended coordinator run. Owner-scoped. No request body.

Response `200 OK` with the current outcome spec (same shape as `GET /api/runs/{id}/outcome-spec`, or `null` if not yet readable).

`400 Bad Request` when `id` is not a valid run id.
`403 Forbidden` when the caller does not own the run.
`404 Not Found` when the run does not exist.
`409 Conflict` with `error: "run_not_active"` when no live coordinator run is registered for the id.
`409 Conflict` with `error: "no_pending_gate"` when the spec is not currently awaiting confirmation (for example, already confirmed).

### POST /api/runs/{id}/outcome-spec/revise

Requests a revision of the drafted outcome spec. The coordinator re-drafts using the feedback and re-suspends at the gate. Owner-scoped.

Request:

```json
{ "feedback": "Tighten the scope to a single device for now" }
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `feedback` | string | Yes | Revision guidance for the coordinator. |

Response `200 OK` with the current outcome spec (same shape as `GET /api/runs/{id}/outcome-spec`, or `null` if not yet readable).

`400 Bad Request` when `id` is not a valid run id or `feedback` is missing.
`403 Forbidden` when the caller does not own the run.
`404 Not Found` when the run does not exist.
`409 Conflict` with `error: "run_not_active"` when no live coordinator run is registered for the id.
`409 Conflict` with `error: "no_pending_gate"` when the spec is not currently awaiting confirmation.

### The orchestration lifecycle

Confirming the outcome spec advances the coordinator run through Phase 2: **confirm -> decompose -> dispatch -> observe -> steer**. After confirmation, the coordinator decomposes the spec into a work plan (subtasks plus dependency edges), dispatches the ready subtasks as child runs (independent subtasks in parallel, dependent ones serialized behind their prerequisites), observes each child's read-only timeline, and relays any steering direction to the running subagents. The work plan, child runs, and steering directives are read and driven through the endpoints below; the live graph streams as `coordinator.work_plan`, `coordinator.topology`, `subtask.*`, and `coordinator.steering` events on the coordinator run's own `GET /api/runs/{id}/stream`.

### GET /api/runs/{coordinatorRunId}/work-plan

Returns the work plan for a coordinator run: the decomposed subtasks and the dependency edges between them. Owner-scoped. Returns `null` (or `404`) before the coordinator has drafted a plan.

Response `200 OK`:

```json
{
  "workPlanId": "a1b2c3d4-...",
  "coordinatorRunId": "f36800fd-...",
  "outcomeSpecId": "9e8d7c6b-...",
  "status": "dispatching",
  "statusReason": null,
  "subtasks": [
    {
      "subtaskId": 5,
      "title": "Add session persistence to the onboarding store",
      "scope": "Persist partial onboarding state",
      "assignedAgent": "morpheus",
      "selectedModelId": "gpt-4o",
      "phase": "execution",
      "isolation": "worktree",
      "status": "running",
      "childRunId": "7c1f..."
    }
  ],
  "dependencies": [
    { "subtaskId": 7, "dependsOnSubtaskId": 5 }
  ]
}
```

| Field | Type | Notes |
| --- | --- | --- |
| `workPlanId` | string | Persisted work plan id. |
| `coordinatorRunId` | string | The coordinator run that owns the plan. |
| `outcomeSpecId` | string | The confirmed outcome spec the plan was decomposed from. |
| `status` | string | `planned`, `dispatching`, `awaiting_assembly`, `assembling`, `in_review`, `complete`, or a parked/terminal state `assembly_blocked` / `assembly_failed` / `assembly_declined`. |
| `statusReason` | string\|null | Human-readable failure reason for a terminal plan, taken from the coordinator run's `result` (for example `assembly_blocked: <reason>`, `assembly_merge_failed: <reason>`, `assembly_error: <message>`). `null` while the plan is non-terminal. The UI can render "Failed: &lt;statusReason&gt;" without a second round-trip. |
| `subtasks` | array | Decomposed units of work; each has `subtaskId`, `title`, `scope`, `assignedAgent`, `selectedModelId`, `phase`, `isolation`, `status`, and `childRunId` (null until dispatched). |
| `dependencies` | array | `{ subtaskId, dependsOnSubtaskId }` edges; a subtask dispatches only once every dependency reaches `assemble_ready`/`completed`. |

`400 Bad Request` when `id` is not a valid run id.
`403 Forbidden` when the caller does not own the run.
`404 Not Found` when the run does not exist or has no work plan yet.

### GET /api/runs/{coordinatorRunId}/children

Lists the child runs dispatched by a coordinator run, one row per subtask that has a child run, each paired with its subtask status. Owner-scoped. Empty array when nothing has been dispatched.

Response `200 OK`:

```json
[
  {
    "subtaskId": 5,
    "childRunId": "7c1f...",
    "subtaskStatus": "running",
    "assignedAgent": "morpheus",
    "selectedModelId": "gpt-4o",
    "childRunStatus": "in_progress",
    "worktreeBranch": "coordinator/5-session-persistence",
    "treeHash": null,
    "stepCount": 12
  }
]
```

| Field | Type | Notes |
| --- | --- | --- |
| `subtaskId` | integer | The subtask this child run executes. |
| `childRunId` | string | The dispatched child run id. |
| `subtaskStatus` | string | The subtask's status in the work plan. |
| `assignedAgent` | string | The roster agent running the subtask. |
| `selectedModelId` | string | The model selected for the subtask. |
| `childRunStatus` | string | The child run's own status. |
| `worktreeBranch` | string | The child run's worktree branch. |
| `treeHash` | string | The committed worktree tree hash once the child reaches assemble-ready; null before then. |
| `stepCount` | integer | Steps observed on the child run so far. |

`400 Bad Request` when `id` is not a valid run id.
`403 Forbidden` when the caller does not own the run.
`404 Not Found` when the run does not exist.

### POST /api/runs/{coordinatorRunId}/steer

Creates a steering directive that the coordinator relays to one or more running subagents. Owner-scoped.

Request:

```json
{
  "kind": "redirect",
  "targetChildRunId": "7c1f...",
  "instruction": "Use the existing session store instead of adding a new table"
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `kind` | string | Yes | `stop`, `redirect`, or `amend`. Pause is not supported in Phase 2. |
| `targetChildRunId` | string | No | The child run to steer; omit to broadcast to every active child. |
| `instruction` | string | Yes | Direction relayed to the targeted subagent(s). |

Response `202 Accepted` with the created directive:

```json
{
  "directiveId": "d4c3b2a1-...",
  "kind": "redirect",
  "targetChildRunId": "7c1f...",
  "status": "pending",
  "instruction": "Use the existing session store instead of adding a new table"
}
```

A `stop` takes effect immediately: it cancels the targeted child run's in-flight turn. A `redirect` or `amend` takes effect at the targeted subagent's next turn boundary, without restarting the run — it is queued and applied when the child's current turn completes (or when it next suspends at a gate). The directive's progress is observable as `coordinator.steering` events (`pending -> queued -> relayed -> applied`) on the coordinator run stream.

`400 Bad Request` when `id` is not a valid run id, `kind` is not one of `stop`/`redirect`/`amend`, or `instruction` is missing.
`403 Forbidden` when the caller does not own the run.
`404 Not Found` when the run does not exist.
`409 Conflict` with `error: "run_not_active"` when no live coordinator run is registered for the id.

### POST /api/runs/{coordinatorRunId}/assembly/review

The ONE collective human-review gate for Phase 3 collective assembly (Feature 008). After every child subtask finishes, the coordinator builds a single integration branch (all eligible child branches merged in dependency order off the originating branch), runs a collective RAI pass over the aggregate diff, then suspends here for one human decision over the **combined** output of all agents. Mirrors `POST /api/runs/{id}/review` (owner-scoped, at-most-once) but `{id}` is the **coordinator** run id, and the decision is delivered to the service-driven gate the collective pipeline is awaiting. Owner-scoped.

In multi-replica deployments, the reviewer may submit this request to any API replica. If the receiving replica does not own the in-memory assembly pipeline but the durable work plan is still `in_review` at assembly stage `review`, the decision is stored as a deferred decision for the owner replica to pick up and apply to the armed gate. A duplicate submit while that deferred decision exists returns the same accepted response rather than replacing the original decision.

Request:

```json
{
  "approved": false,
  "request_changes": true,
  "feedback": "The change in src/auth/login.ts breaks logout",
  "target_files": ["src/auth/login.ts"]
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `approved` | bool | Yes | `true` continues to the ONE collective merge → ONE collective scribe → `complete`. |
| `request_changes` | bool | No | When `true` (and `approved` is `false`), the coordinator re-dispatches the affected children rather than declining. |
| `feedback` | string | No | Free-text reviewer feedback; path-like tokens are parsed to infer which children to redo. |
| `target_files` | string[] | No | Explicit list of files the changes should target; augments the tokens parsed from `feedback`. |

**Decision routing**

- **Approve** (`approved: true`) → the pipeline merges the integration branch into the originating branch and runs the collective scribe, emitting `coordinator.assembly_merge_*`, `coordinator.assembly_scribe_*`, then `coordinator.assembly_completed`; the work plan reaches `complete`.
- **Request changes** (`approved: false`, `request_changes: true`) → the coordinator infers the affected children from `target_files` ∪ path tokens in `feedback`, intersects them with each child's persisted touched-files, expands to include dependents, resets those subtasks to `pending` (leaving the rest intact), returns the plan to `dispatching`, and re-dispatches. If no file can be inferred or no child matches, it falls back to re-dispatching **all** children. Emits `coordinator.assembly_changes_requested`.
- **Decline** (`approved: false`, `request_changes: false`) → terminal `assembly_declined`; the coordinator emits `coordinator.assembly_declined` (`reason`, `reviewer`), the work plan moves to `assembly_declined`, the run ends `declined`, and the coordinator stream closes.

When the pipeline arms this gate it emits `coordinator.assembly_review_requested` on the coordinator stream with `integrationBranch`, `treeHash` (the assembled integration tree hash), `includedSubtaskIds` (which subtasks the assembled output covers), `raiSafetyFlagged`, and `hasChanges` — the UI subscribes to this to know a collective human review is being requested and to render the assembled output. If the assembly background task hits an unexpected fault it emits `coordinator.assembly_failed` (`reason`, `phase`) and the run ends `failed` with `result: "assembly_error: &lt;message&gt;"`.

Response `200 OK`:

```json
{ "runId": "f36800fd-...", "accepted": true }
```

`400 Bad Request` when `id` is not a valid run id.
`403 Forbidden` when the caller does not own the run, or does not own the pending review request.
`404 Not Found` when the run does not exist.
`409 Conflict` with `error: "no_assembly_review_pending"` when no collective review is currently awaited for the run (the pipeline has not reached the gate yet, or the decision was already consumed and the work plan has left `in_review`).

### GET /api/runs/{id}/assembly/files

Lists files in the coordinator assembly workspace. Owner-scoped.

### GET /api/runs/{id}/assembly/files/{**path}

Returns diff/content metadata for a specific file in the assembly workspace. Owner-scoped.

### GET /api/runs/{id}/assembly/workspace

Returns the assembly workspace tree. Owner-scoped.

### GET /api/runs/{id}/assembly/content/{**path}

Returns raw file content from the assembly workspace. Owner-scoped.



### GET /auth/github/authorize

Begins the GitHub OAuth redirect flow. This endpoint is anonymous and redirects to GitHub. If GitHub OAuth is not configured it returns `503`.

### GET /auth/github/callback

Receives the GitHub OAuth callback, exchanges the code for a token, and redirects to the configured frontend with `auth=success` or `auth=error`.

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
{ "status": "signed_in", "login": "octocat", "avatar_url": "https://avatars.githubusercontent.com/u/..." }
```

| `status` value | Meaning |
| --- | --- |
| `signed_in` | A valid token is stored; `login` contains the GitHub username |
| `signed_out` | The user explicitly signed out |
| `never_signed_in` | No sign-in has been completed for this user |

### GET /api/github/repos

Lists repositories for the signed-in GitHub user. Response `200 OK` is an array of:

```json
{ "full_name": "owner/repo", "description": "string", "private": true, "default_branch": "main" }
```

Returns `401 Unauthorized` when no valid GitHub access token is stored.

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

### GET /api/projects/{id}/casting/universes

Lists allowed universe names for a project.

Response `200 OK`:

```json
{ "universes": ["star-wars", "marvel"] }
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

### GET /api/projects/{id}/casting/proposals

Lists active proposals for the project. Response `200 OK` is an array of `CastProposalDto` objects.

### GET /api/projects/{id}/casting/proposals/{proposalId}

Returns the current state of a proposal.

Response `200 OK` — a `CastProposalDto` (same shape as the `POST` response above).

Returns `404 Not Found` when no proposal exists for the given id.

### PATCH /api/projects/{id}/casting/proposals/{proposalId}

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

### POST /api/projects/{id}/casting/proposals/{proposalId}/confirm

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

### DELETE /api/projects/{id}/casting/proposals/{proposalId}

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

Response `200 OK` returns `{ "member_name": "...", "content": "..." }`. Returns `404 Not Found` when the member does not exist.

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

### GET /api/projects/{projectId}/team/sync

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

`changes` is an empty array and `nothing_to_sync` is `true` when there is nothing to commit. `change_set_hash` must be passed to `POST /api/projects/{projectId}/team/sync` to prevent stale commits.

### POST /api/projects/{projectId}/team/sync

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
| `expected_change_set_hash` | string | Yes | Hash from `GET /api/projects/{projectId}/team/sync`. The server rejects the commit if the change set has shifted since you fetched it. |
| `message` | string | No | Commit message. A default message is used when omitted. |

Returns `409 Conflict` with `error: "sync_state_changed"` when the change set hash does not match. Fetch a fresh hash from `GET /api/projects/{projectId}/team/sync` and retry.

Response `200 OK` returns `{ "commit_id": "..." }`.

## Persistence

SQLite tables are created on startup with WAL enabled:

| Table | Purpose |
| --- | --- |
| `runs` | Run records with status, timing, submitting user, task, model source, model id, project id, and the final result text |
| `projects` | Project records with name, origin, working directory, default branch, owner, provider settings, and state |
| `github_tokens` | Per-user GitHub tokens stored by the OS credential store (not a SQLite table — managed by `OsCredentialStoreGitHubTokenStore`) |

The run's event stream is held in memory by `RunStreamStore` and is not persisted to SQLite. After a process restart, the granular event history is unavailable — only the final `result` text survives. Completed runs are persisted via the Copilot SDK session store (session ID = `agentweaver-run-{runId}`). The `GET /api/runs/{id}/history` endpoint replays persisted session events for terminal runs.

## Configuration keys

### Core storage and git keys

| Key | Default | Purpose |
| --- | --- | --- |
| `Database:Path` | `agentweaver.db` in the app data directory | SQLite database file |
| `Worktrees:BasePath` | `worktrees` in the app data directory | Root folder for run worktrees |
| `Git:Author:Name` | `Agentweaver` | Author name for commits and merges |
| `Git:Author:Email` | `agentweaver@localhost` | Author email for commits and merges |
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
