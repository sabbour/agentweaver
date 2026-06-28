# MCP server reference

::: warning Experimental
The Agentweaver MCP server is **experimental**. Tool names, parameters, and behavior may change without notice. Pin to a known revision if you depend on the current surface.
:::

The Agentweaver MCP server exposes all Agentweaver operations as structured tool calls over stdio. Any MCP-capable host (GitHub Copilot CLI, Claude, Cursor, Windsurf, etc.) can discover and invoke these tools automatically via the `.mcp.json` file at the repository root.

## Setup

Set the required environment variable before starting any MCP host that uses the server:

```
AGENTWEAVER_API_KEY=<your-api-key>
```

Optionally override the API base URL (defaults to `http://localhost:5000`):

```
AGENTWEAVER_API_URL=http://localhost:5000
```

The `.mcp.json` at the repository root registers the server automatically for MCP hosts that support auto-discovery (Copilot CLI ≥1.0.59 and equivalents). No manual registration is required beyond setting the environment variable.

## Authentication

The MCP server forwards every tool call to the Agentweaver API as an authenticated HTTP request using a **bearer token** (`Authorization: Bearer <key>`).

- **Shared key (default).** `AGENTWEAVER_API_KEY` is used for outbound API calls when no per-caller key is present.
- **Per-caller key propagation.** When the MCP server itself is reached over HTTP with a bearer token (validated by `McpBearerTokenMiddleware`), that caller's key is stashed on the request (`HttpContext.Items["mcp.api_key"]`) and `AgentweaverApiClient.GetEffectiveApiKey()` uses it for the downstream API call — so each caller's identity flows through to the API rather than collapsing onto the shared key. SSE streams (`run_watch`) propagate the same effective key. When no per-caller key is present, the shared `AGENTWEAVER_API_KEY` is used.

## Health probe

The MCP server exposes an unauthenticated liveness probe:

```
GET /healthz → 200 { "status": "healthy" }
```

`/healthz` is explicitly bypassed by the bearer-token middleware so orchestrators (containers, Kubernetes probes) can check liveness without a key.

## Error handling

All tools surface API errors as MCP tool errors with human-readable messages. HTTP 4xx errors include the API's error detail. HTTP 5xx errors are distinguished from client-side failures.

---

## Projects

### `project_list`

List all Agentweaver projects.

**Parameters**: none

**Returns**: Array of project objects with `id`, `name`, `repository_path`, and status.

---

### `project_get`

Get a project by ID.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Full project object.

---

### `project_create`

Create a new project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | yes | Project name |
| `working_directory` | string | yes | Absolute path to the local working directory |
| `origin` | string | no | Project origin: `blank` (default) or `github` |
| `blueprint_id` | string | no | Predefined blueprint ID to apply (exclusive with `blueprint`) |
| `blueprint` | object | no | Inline blueprint JSON object to apply (exclusive with `blueprint_id`) |

**Returns**: Created project object with assigned `id`.

---

### `project_rename`

Rename a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `name` | string | yes | New name |

**Returns**: Updated project object.

---

### `project_relink`

Update the working directory for a project (e.g., after moving the repository on disk).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `working_directory` | string | yes | New absolute path to the working directory |

**Returns**: Updated project object.

---

### `project_delete`

Delete a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Confirmation message.

---

### `project_configure`

Update provider settings for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `default_provider` | string | yes | Model provider (`github_copilot` or `microsoft_foundry`) |
| `default_model_github_copilot` | string | no | Model ID for GitHub Copilot provider |
| `default_model_microsoft_foundry` | string | no | Model ID for Microsoft Foundry provider |

**Returns**: Confirmation message.

---

## Runs

### `run_submit`

Submit a new agent run for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task` | string | yes | Task description for the agent |
| `agent_name` | string | no | Target team member name (e.g., `"ripley"`) |
| `base_branch` | string | no | Branch to base the run on (defaults to current) |
| `model_source` | string | no | Model provider override |

**Returns**: `{ run_id, status }`.

---

### `run_status`

Get the current status and details of a run.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |

**Returns**: Run object with `status`, `task`, `agent_name`, `started_at`, `result` (when the run completes with no changes: `"no_changes"`), `diff` (when in review), and outcome fields.

Possible `status` values: `pending`, `in_progress`, `awaiting_review`, `merging`, `merged`, `declined`, `failed`, `merge_failed`.

---

### `run_watch`

Watch a run live. Streams agent messages and tool call events as MCP progress notifications until the run completes, then returns the final run state.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |

**Returns**: Final run object (same as `run_status`).

**Progress notifications** are emitted for:
- `agent.message` / `agent.message.delta` — agent output text
- `tool.call` — tool the agent is invoking
- `tool.result` — tool call outcome
- `review.requested` — run is ready for review

---

### `run_review`

Approve or decline a completed run.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |
| `approved` | boolean | yes | `true` to merge, `false` to decline |

**Returns**: Review outcome with `status` and `merge_result` (commit hash when merged).

---

### `run_show_artifacts`

List files changed by a completed run.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |

**Returns**: Array of file paths changed in the run's worktree.

---

### `run_get_file`

Get the content or diff of a specific file from a run's worktree.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |
| `path` | string | yes | Relative path to the file within the repository |

**Returns**: File content or diff.

---

## Coordinator

Thin proxies over the Coordinator endpoints. The Coordinator agent drafts a confirmable outcome spec for a goal, then suspends at a confirmation gate. No subagent work is dispatched until the spec is confirmed. A coordinator run is an ordinary run, so its live drafting is observable with `run_watch` (see below).

### `coordinator_start`

Start a coordinator orchestration for a project from a plain-language goal. Proxies `POST /api/projects/{id}/orchestrations`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `goal` | string | yes | The outcome the coordinator should draft a spec for |
| `model_id` | string | no | Model id override; falls back to the project default, then the role default |

**Returns**: `{ runId }` for the new coordinator run.

---

### `coordinator_outcome_spec_get`

Get the current persisted outcome spec for a coordinator run. Proxies `GET /api/runs/{id}/outcome-spec`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |

**Returns**: Outcome spec object with `goal`, `desiredOutcome`, `scope`, `assumptions`, `clarifyingQuestions` (omitted when none), `status` (`drafting`, `awaiting_confirmation`, `confirmed`, or `declined`), and `confirmedBy` (set once confirmed).

---

### `coordinator_outcome_spec_confirm`

Confirm the drafted outcome spec, resuming the suspended coordinator run past the confirmation gate. Proxies `POST /api/runs/{id}/outcome-spec/confirm`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |

**Returns**: The current outcome spec (same shape as `coordinator_outcome_spec_get`), or `null` if not yet readable. Surfaces `409` errors `run_not_active` and `no_pending_gate` as tool errors.

---

### `coordinator_outcome_spec_revise`

Request a revision of the drafted outcome spec. The coordinator re-drafts using the feedback and re-suspends at the gate. Proxies `POST /api/runs/{id}/outcome-spec/revise`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |
| `feedback` | string | yes | Revision guidance for the coordinator |

**Returns**: The revised outcome spec (same shape as `coordinator_outcome_spec_get`), or `null` if not yet readable. Surfaces `409` errors `run_not_active` and `no_pending_gate` as tool errors.

---

### `coordinator_work_plan_get`

Get the work plan for a coordinator run: the decomposed subtasks and the dependency edges between them. Proxies `GET /api/runs/{id}/work-plan`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |

**Returns**: Work plan object with `workPlanId`, `coordinatorRunId`, `outcomeSpecId`, `status`, `subtasks` (each with `subtaskId`, `title`, `scope`, `assignedAgent`, `selectedModelId`, `phase`, `isolation`, `status`, `childRunId`), and `dependencies` (`{ subtaskId, dependsOnSubtaskId }` edges). `null` before a plan is drafted.

---

### `coordinator_children_get`

List the child runs dispatched by a coordinator run, each paired with its subtask status. Proxies `GET /api/runs/{id}/children`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |

**Returns**: Array of child rows, each with `subtaskId`, `childRunId`, `subtaskStatus`, `assignedAgent`, `selectedModelId`, `childRunStatus`, `worktreeBranch`, `treeHash`, and `stepCount`. Empty when nothing has been dispatched.

---

### `coordinator_steer`

Steer a coordinator run's subagents. Proxies `POST /api/runs/{id}/steer`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |
| `kind` | string | yes | `stop`, `redirect`, or `amend` |
| `instruction` | string | yes | Direction relayed to the targeted subagent(s) |
| `target_child_run_id` | string | no | Target child run ID; omit to broadcast to every active child |

A `stop` cancels the targeted child run's in-flight turn immediately. A `redirect` or `amend` takes effect at the targeted subagent's next turn boundary, without restarting the run. Pause is not supported in Phase 2.

**Returns**: The created steering directive with `directiveId`, `kind`, `targetChildRunId`, `status` (`pending`), and `instruction`.

---

### `orchestration_topology`

Get a one-shot topology snapshot for a coordinator run by combining the work plan and child runs. Proxies `GET /api/runs/{id}/work-plan` and `GET /api/runs/{id}/children`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Coordinator run ID |

**Returns**: `{ coordinatorRunId, workPlan, children }` — the current work plan (subtasks and dependency edges) alongside the dispatched child runs. For the live graph, use `run_watch` (see below).

---

### Watching a coordinator run

There is no separate streaming tool for the coordinator. A coordinator run is an ordinary run, so point the existing [`run_watch`](#run_watch) tool at the coordinator `run_id` to observe live drafting and orchestration. The `coordinator.started`, `coordinator.outcome_spec`, and `coordinator.outcome_spec.confirmed` events ride the same `sequence`-ordered run stream, and Phase 2 adds `coordinator.work_plan`, `coordinator.topology` (a `version: 1` snapshot at `seq: 0` followed by deltas), `subtask.*`, and `coordinator.steering` on that same stream. The live orchestration graph is reconstructable from `run_watch` alone — no extra streaming tool is needed. Use `coordinator_outcome_spec_get`, `coordinator_work_plan_get`, `coordinator_children_get`, or `orchestration_topology` for an authoritative point-in-time snapshot.

---

## Team

### `team_get`

Get the current team roster for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Team object with `members` array, each with `name`, `role`, and `status`.

---

### `team_cast`

Cast a team for a project. Supports a single-call flow (create + confirm) or a two-step flow (create proposal, inspect, then confirm separately).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `goal` | string | conditional | Goal description for the new team (required unless `confirm_proposal_id` is set) |
| `confirm_proposal_id` | string | conditional | ID of an existing proposal to confirm (skips creation) |
| `confirm` | boolean | no | Automatically confirm the newly created proposal (default `false`) |
| `mode` | string | no | Casting mode: `free_text` (default), `scenario`, `analysis`, or `manual` |
| `intent` | string | no | Confirmation intent: `new` (default, replaces team) or `merge` (adds to existing) |

**Returns**: Proposal object (when `confirm=false`) or confirmed team object (when `confirm=true` or `confirm_proposal_id` is set).

---

### `team_member_add`

Add a new member to a project team.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `name` | string | yes | Member name (cast name, lowercase) |
| `role_id` | string | yes | Role ID from the catalog |
| `model_id` | string | no | Model ID override for this member |

**Returns**: Updated team member entry.

---

### `team_member_retire`

Retire a team member.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `member_name` | string | yes | Member name to retire |

**Returns**: Confirmation message.

---

### `team_member_get_charter`

Get a team member's charter document.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `member_name` | string | yes | Member name |

**Returns**: Charter text.

---

## GitHub authentication

### `github_status`

Check the current GitHub authentication status.

**Parameters**: none

**Returns**: Authenticated identity, or unauthenticated status.

---

### `github_signin`

Sign in to GitHub using the device flow. The tool initiates the flow and returns the user code and verification URL immediately. The user opens the URL in a browser and enters the code. The tool then polls until the browser step completes and returns a success confirmation, or times out after two minutes.

**Parameters**: none

**Returns**: On initiation: `{ user_code, verification_uri }`. On completion: authenticated identity.

**Progress notifications** are emitted while polling: `"Waiting for browser authentication..."`.

---

### `github_signout`

Sign out of GitHub.

**Parameters**: none

**Returns**: Confirmation message.

---

## Sandbox policy

### `sandbox_policy_get`

Get the sandbox policy for a repository.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `repository_path` | string | no | Repository path to get the policy for (resolved from project when omitted) |

**Returns**: Current sandbox policy object with `shell_enabled`.

---

### `sandbox_policy_set`

Update the sandbox policy.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `repository_path` | string | yes | Repository path |
| `shell_enabled` | boolean | yes | Whether shell access is enabled for agent runs |

**Returns**: Confirmation message.

---

## Catalog

### `catalog_list_roles`

List all available agent roles.

**Parameters**: none

**Returns**: Array of role definitions with `name`, `description`, and default model.

---

### `catalog_list_scenarios`

List all available casting scenario templates.

**Parameters**: none

**Returns**: Array of scenario templates with `id`, `name`, `description`, and team shape.

---

## Memory

Memory is scoped to projects. Agents use the inbox to submit learnings; the coordinator merges them into decisions. `memory_export` writes the live DB state to `.squad/` and `.agentweaver/context/` files for Squad CLI interoperability.

### `decision_inbox_submit`

Submit a decision or learning to the agent inbox.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent submitting the entry |
| `slug` | string | yes | Unique slug for idempotency (e.g. `prefer-async`) |
| `type` | string | yes | `learning` \| `pattern` \| `update` \| `architectural` \| `scope` \| `process` \| `technical` |
| `title` | string | yes | Short title |
| `content` | string | yes | Full content |
| `rationale` | string | no | Optional rationale |

**Returns**: Created inbox entry with `id` and `status: "pending"`.

---

### `decision_inbox_list`

List inbox entries for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent` | string | no | Filter by agent name |
| `type` | string | no | Filter by entry type |
| `status` | string | no | `pending` (default) \| `merged` \| `rejected` |

**Returns**: Array of inbox entries.

---

### `decision_inbox_merge`

Merge a pending inbox entry into team decisions.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `entry_id` | string | yes | Inbox entry ID |

**Returns**: Resulting decision object.

---

### `decision_inbox_reject`

Reject a pending inbox entry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `entry_id` | string | yes | Inbox entry ID |

**Returns**: `"rejected"`.

---

### `decision_create`

Create a team decision directly (coordinator path).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent recording the decision |
| `type` | string | yes | `architectural` \| `scope` \| `process` \| `technical` |
| `title` | string | yes | Short title |
| `content` | string | yes | Full content |
| `rationale` | string | no | Optional rationale |

**Returns**: Created decision object.

---

### `squad_decide`

Submit a team decision to the decision inbox from a squad agent. A convenience over `decision_inbox_submit` for agents recording a decision they want the coordinator to review and merge.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent submitting the decision |
| `slug` | string | yes | Unique kebab-case slug for idempotency |
| `type` | string | yes | `architectural` \| `scope` \| `process` \| `technical` \| `learning` \| `pattern` \| `update` |
| `title` | string | yes | Short title |
| `content` | string | yes | Full content |
| `rationale` | string | no | Optional rationale |

**Returns**: Created inbox entry with `id` and `status: "pending"`.

---

### `decision_list`

List team decisions for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `type` | string | no | Filter by type |
| `agent` | string | no | Filter by agent name |

**Returns**: Array of decision objects.

---

### `decision_update`

Update a decision's status or content.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `decision_id` | string | yes | Decision ID |
| `status` | string | no | `active` \| `superseded` \| `archived` |
| `content` | string | no | New content |
| `superseded_by_id` | string | no | ID of the superseding decision |

**Returns**: Updated decision object.

---

### `memory_record`

Add a memory entry for an agent.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent name |
| `type` | string | yes | `learning` \| `pattern` \| `core_context` \| `update` |
| `content` | string | yes | Content |
| `importance` | string | no | `low` \| `medium` (default) \| `high` |
| `tags` | string | no | Comma-separated tags |

**Returns**: Created memory entry.

---

### `memory_list`

List memory entries for a specific agent.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent name |
| `type` | string | no | Filter by type |
| `importance` | string | no | Filter by importance |

**Returns**: Array of memory entries.

---

### `memory_get`

Get a single memory entry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent name |
| `memory_id` | string | yes | Memory entry ID |

**Returns**: Memory entry object.

---

### `memory_search`

Cross-agent memory search across the whole project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `type` | string | no | Filter by type |
| `tags` | string | no | Comma-separated tags (OR semantics) |

**Returns**: Array of memory entries from all agents.

---

### `session_start`

Start a new work session. Auto-ends any existing open session.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `session_id` | string | yes | Unique session ID |
| `focus_area` | string | yes | Current focus description |
| `active_issues` | string | no | Active issues being worked |

**Returns**: Created session object.

---

### `session_current`

Get the current open session for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Current session object or `null`.

---

### `session_update`

Update focus, summary, or end the current session.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `focus_area` | string | no | New focus area |
| `active_issues` | string | no | Active issues |
| `summary` | string | no | Text to append to the session summary |
| `end` | boolean | no | `true` to close the session |

**Returns**: `"updated"`.

---

### `memory_export`

Export project memory to `.squad/` and `.agentweaver/context/` files for Squad CLI interoperability.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: `"exported"`.

---

### `memory_import`

Import `.squad/decisions/inbox/*.md` files from disk into the project memory database.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: `"imported"`.

---

## Runs (continued)

### `run_retry`

Retry a failed run by creating a fresh run from its original inputs.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID to retry |

**Returns**: `"Retried run {run_id} -> new run {new_run_id}."` — confirmation with the new run ID.

---

### `run_archive`

Archive a run off the active project board.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |

**Returns**: Updated run object.

---

### `project_list_runs`

List all runs for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Array of run objects with `run_id`, `status`, `task`, `agent_name`, and timing fields.

---

## Backlog

The backlog is the project's Kanban board for task management. Tasks progress through Backlog → Ready → Active, with terminal states of Done, Failed, and Archived.

### `backlog_capture_task`

Capture a new task into the project backlog.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `title` | string | yes | Task title |
| `description` | string | no | Task description |

**Returns**: Created task object with `id`, `title`, `description`, and `status: "backlog"`.

---

### `backlog_edit_task`

Edit the title and/or description of a backlog task.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task_id` | string | yes | Task ID |
| `title` | string | yes | New title |
| `description` | string | no | New description (omit to clear) |

**Returns**: Updated task object.

---

### `backlog_delete_task`

Delete a backlog task. Fails with `409` if the task has already been claimed.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task_id` | string | yes | Task ID |

**Returns**: `"Task deleted successfully."`.

---

### `backlog_move_to_ready`

Move a task from Backlog to Ready, optionally at a specific position.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task_id` | string | yes | Task ID |
| `target_index` | integer | no | Zero-based target position in Ready column (appends to end when omitted) |

**Returns**: Updated task object with `status: "ready"`.

---

### `backlog_move_to_backlog`

Move a task from Ready back to Backlog, optionally at a specific position.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task_id` | string | yes | Task ID |
| `target_index` | integer | no | Zero-based target position in Backlog column (appends to end when omitted) |

**Returns**: Updated task object with `status: "backlog"`.

---

### `backlog_reorder_task`

Reorder a task within its current bucket (Backlog or Ready) to a new zero-based position.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task_id` | string | yes | Task ID |
| `target_index` | integer | yes | Zero-based target position within the task's current bucket |

**Returns**: Updated task object.

---

### `backlog_get_board`

Get the full Kanban board for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `include_terminal_history` | boolean | no | Include terminal/done history (default `false`) |

**Returns**: Board object with columns: `backlog`, `ready`, `problems`, `human_review`, `active`, and `done`. Each column is an array of task cards with `id`, `title`, `description`, `status`, and linked run details.

---

### `backlog_archive_task`

Archive a backlog task off the active board.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task_id` | string | yes | Task ID |

**Returns**: Updated task object with `status: "archived"`.

---

### `backlog_get_workflow_stages`

Get the ordered canonical run-bucket definitions for a project (Problems, Human Review, Active, Done).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Array of workflow stage definitions, each with `name`, `label`, and `terminal` flag.

---

### `backlog_get_settings`

Get the per-project backlog pickup settings.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Settings object with `max_ready_per_heartbeat`, `pickup_autopilot`, and `pickup_auto_approve_tools`.

---

### `backlog_set_settings`

Set the per-project backlog pickup settings.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `max_ready_per_heartbeat` | integer | yes | Maximum Ready tasks claimed per heartbeat tick (1–20) |
| `pickup_autopilot` | boolean | yes | Auto-answer clarifying questions during unattended coordinator runs |
| `pickup_auto_approve_tools` | boolean | yes | Auto-approve allow-with-approval tools during unattended runs |

**Returns**: Updated settings object.

---

### `send_all_backlog_to_ready`

Bulk-promote all Backlog tasks to Ready in one atomic operation.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: `"Promoted N backlog task(s) to Ready."` or `"No backlog tasks to promote."`.

---

### `backlog_decompose_spec`

Read a markdown spec file from the project's workspace, run AI decomposition, and return proposed backlog items. Use `confirm=true` to create the tasks; `confirm=false` (default) previews only. Results are capped at 50 items.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `file_path` | string | yes | Relative path to a markdown file within the project workspace |
| `confirm` | boolean | no | `true` creates the tasks; `false` (default) returns a preview only |

**Returns**: `{ proposed_items: [{ title, description, already_exists }], was_capped, total_found }`. `already_exists` flags items already present in the backlog (dedup by title + source file).

---

## Blueprints

Blueprints are pre-packaged project configurations specifying a team roster, workflow, review policy, and sandbox profile.

### `list_blueprints`

List the predefined Agentweaver blueprints.

**Parameters**: none

**Returns**: Array of blueprint objects, each with `id`, `name`, `description`, `roster`, `workflow`, `review_policy`, and `sandbox_profile`.

---

### `blueprint_generate`

Generate a new blueprint from a natural language description of the team and goals.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `description` | string | yes | Plain-language description of the team and workflow |

**Returns**: Generated blueprint object. Returns `422` if the model output cannot be validated.

---

### `validate_blueprint`

Validate a blueprint object against the schema and role constraints.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `blueprint` | object | yes | Blueprint JSON object with `id`, `name`, `description`, `roster`, `workflow`, `review_policy`, `sandbox_profile` |

**Returns**: `{ "valid": true, "errors": [] }` on success, or `{ "valid": false, "errors": [...] }` with a list of validation errors.

---

## Diagnostics

### `diagnostics_get`

Get a real-time system diagnostics snapshot.

**Parameters**: none

**Returns**: Object with `api_version`, `uptime`, `project_count`, `active_run_count`, `heartbeat_state`, and `checkpoint_gc_state`.

---

### `heartbeat_status`

Get the current coordinator heartbeat service status.

**Parameters**: none

**Returns**: Object with `enabled`, `interval_seconds`, `last_tick_at`, and `service_state` (`running` | `waiting_first_tick` | `disabled`).

---

## Workflows

### `workflows_list`

List all discovered workflow definitions for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Array of workflow summaries with `id`, `name`, `valid`, `validation_errors`, and `is_default`.

---

### `workflow_get`

Get the full definition of a single workflow by ID.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `workflow_id` | string | yes | Workflow ID |

**Returns**: Full workflow definition with `id`, `name`, `trigger`, `nodes`, and `edges`.

---

### `workflows_sync`

Re-read the project's workflow definitions from disk, refreshing the in-memory registry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Updated workflow list (same shape as `workflows_list`).

---

### `workflow_generate`

Generate a new workflow YAML **draft** from a natural-language description. Nothing is written to disk — use `workflow_save` to persist (FR-065).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `description` | string | yes | Plain-language description of the pipeline |

**Returns**: `{ yaml, workflow_id, was_corrected }` — the draft YAML, a suggested id (matching the `id` field), and whether a correction pass was applied.

---

### `workflow_save`

Persist a workflow YAML to the project workspace (`.agentweaver/workflows/`). Validates and dry-run binds every node before writing; on success the workflow is immediately coordinator-selectable.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `workflow_id` | string | yes | Workflow ID (must match the `id` in the YAML) |
| `yaml` | string | yes | Workflow YAML to save |

**Returns**: The full `WorkflowDefinitionDto` (id, nodes, edges, trigger, validation status). Returns `400` on YAML parse errors, an unwired node type, or an `id`/route mismatch.

---

## Workspace

Browse the git-backed project workspace. Supports reading files at any branch or run worktree ref.

### `list_project_workspace_refs`

List the browsable git refs for a project workspace: the base branch and any active run worktrees.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Object with `base_branch` (string) and `worktrees` (array of `{ branch, run_id }`).

---

### `list_project_workspace`

List the flat file tree for a project workspace at a given ref.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `ref` | string | no | Branch name or worktree branch to browse (defaults to base branch) |

**Returns**: Array of workspace node objects, each with `path`, `type` (`blob` or `tree`), and `size`.

---

### `get_project_workspace_file`

Get the content of a file in a project workspace at a given ref.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `path` | string | yes | Relative file path within the workspace (forward slashes, e.g. `src/main.cs`) |
| `ref` | string | no | Branch name or worktree branch (defaults to base branch) |

**Returns**: Object with `path`, `content` (base64-encoded), `encoding`, and `size`.
