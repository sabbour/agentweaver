# MCP server reference

::: warning Experimental
The Agentweaver MCP server is **experimental**. Tool names, parameters, and behavior may change without notice. Pin to a known revision if you depend on the current surface.
:::

The Agentweaver MCP server exposes all Agentweaver operations as structured tool calls over stdio. Any MCP-capable host (GitHub Copilot CLI, Claude, Cursor, Windsurf, etc.) can discover and invoke these tools automatically via the `.mcp.json` file at the repository root.

> For a complete, always-up-to-date list of every tool name and its one-line description, see the auto-generated [MCP tool index](./mcp-tools.md). This page documents each tool's full parameters and return shape.

## Setup

Set an Agentweaver broker token before starting a local stdio MCP host:

```
AGENTWEAVER_TOKEN=<agentweaver-broker-token>
```

`AGENTWEAVER_TOKEN` must be issued by Agentweaver for the exact
`<public-origin>/mcp` audience and include `mcp:invoke`. The API attributes calls to its
subject and enforces project ownership.

::: danger Broker tokens only
Raw Entra access tokens, GitHub tokens, API keys, and shared service credentials are not MCP
credentials. Stdio mode refuses to start without a configured broker token.
:::

Optionally override the API base URL (defaults to `http://localhost:5000`):

```
AGENTWEAVER_API_URL=http://localhost:5000
```

The `.mcp.json` at the repository root registers the server automatically for MCP hosts that support auto-discovery (Copilot CLI ≥1.0.59 and equivalents). No manual registration is required beyond setting the environment variable.

### Using with GitHub Copilot CLI

**Local (stdio), working in this repo.** No setup beyond the environment variable above —
`copilot` auto-discovers the workspace `.mcp.json` and starts
`dotnet run --project apps/Agentweaver.Mcp -- --stdio` on demand. Confirm the tools are
live with `copilot mcp list` or `/mcp` inside an interactive session.

**Hosted/remote (HTTP), e.g. a staging or production deployment.** Register the server
explicitly with a bearer token, since there is no `.mcp.json` entry for a remote host:

```bash
copilot mcp add aw-remote \
  --transport http \
  --url https://<your-agentweaver-host>/mcp \
  --header "Authorization: Bearer <token>"
```

Use `copilot mcp get aw-remote` / `copilot mcp remove aw-remote` to inspect or remove it.
This registration is saved to `~/.copilot/mcp-config.json` and persists across sessions.

**Session-scoped override**, e.g. for a one-off run against a different host without
touching persisted config, use `--additional-mcp-config` with an inline JSON string or an
`@<path-to-json>` file:

```bash
copilot -p "..." --allow-all-tools --additional-mcp-config @aw-mcp-config.json
```

```json
{
  "mcpServers": {
    "aw-remote": {
      "type": "http",
      "url": "https://<your-agentweaver-host>/mcp",
      "headers": { "Authorization": "Bearer <token>" },
      "tools": ["*"]
    }
  }
}
```

`--additional-mcp-config` augments (does not replace) whatever config already exists for
that session only.

::: tip Server-name collisions
Copilot CLI resolves MCP servers by **name**, merging `~/.copilot/mcp-config.json` (user),
`.mcp.json`/`.github/mcp.json` (workspace), and `--additional-mcp-config` (session) in that
order. If your personal `~/.copilot/settings.json` has `agentweaver` listed under
`disabledMcpServers` (e.g. because you disabled the workspace stdio server), naming a
session override `agentweaver` will be silently skipped — check
`~/.copilot/logs/process-*.log` for `Skipping disabled MCP server: <name>` if a
registered server discovers zero tools. Use a distinct name (like `aw-remote` above) to
avoid the collision.
:::

## Authentication

The MCP server forwards every tool call to the Agentweaver API as an authenticated HTTP request using a **bearer token** (`Authorization: Bearer <key>`).

- **HTTP mode.** ASP.NET/OpenIddict validates the broker JWT through remote discovery/JWKS,
  requiring the exact issuer and audience, keyed RS256 signature, valid lifetime, subject, and
  `mcp:invoke`. Only that validated token is forwarded to the API.
- **Stdio mode.** The configured `AGENTWEAVER_TOKEN` is forwarded. The API performs the same broker
  validation before any `PlatformOrMcp` endpoint runs.
- **No fallback.** Raw Entra, GitHub, API-key, malformed, expired, wrong-audience, wrong-issuer,
  wrong-scope, and unknown-key credentials are rejected.

Both RFC 9728 endpoints are anonymous:

- `/.well-known/oauth-protected-resource`
- `/.well-known/oauth-protected-resource/mcp`

They advertise the exact `<public-origin>/mcp` resource, same-origin authorization server, and
`mcp:invoke`. A missing token receives a `401` challenge with `resource_metadata` and `scope` but
no OAuth error. Invalid tokens add `error="invalid_token"`; missing scope adds
`error="insufficient_scope"`.

## Health probe

The MCP server exposes an unauthenticated liveness probe:

```
GET /healthz → 200 { "status": "healthy" }
```

`/healthz` and both RFC 9728 protected-resource metadata paths are explicitly public. MCP protocol
paths require broker authentication.

## Error handling

All tools surface failures in a structured JSON shape:

```json
{
  "error": "Project 'demo' not found.",
  "hint": "Call project_list to see available projects."
}
```

The MCP transport still raises a tool error, but the message is now actionable and consistent. Common mappings include:

- `401` → `Not signed in.` with `Sign in to Agentweaver, then retry.`
- `404` project/run/file lookups → a resource-specific `error` plus the relevant list/read tool in `hint`
- `409` review-state conflicts → `Call run_status to check current state.`
- `-32001`, `408`, or `504` timeouts → `Call diagnostics_get to check health, then retry.`

When the failure is the **run's** outcome rather than the **tool's** outcome, `run_task` returns a normal JSON payload with `status: "failed"` or `status: "timed_out"` instead of throwing a transport error.

## Route parameter encoding

MCP tools treat every route path parameter as data, not as part of the URL structure. Before forwarding a tool call to the Agentweaver API, tool implementations URI-escape path segments such as `project_id`, `run_id`, `agent_name`, backlog task ids, workflow ids, and session ids with `Uri.EscapeDataString()`. This means a crafted identifier containing `../`, `/`, or other reserved path characters cannot traverse to another endpoint or alter the route being called. Query-string parameters are not part of this path hardening and continue to use normal query encoding.

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

Create a new project, optionally cloning a Repo App-authorized GitHub repository and applying a blueprint.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | yes | Project name |
| `working_directory` | string | yes | Absolute path to the local working directory |
| `origin` | string | no | Project origin: `blank` (default) or `github` |
| `repository_selection_code` | string | no | Short-lived opaque code from `github_repository_selection_issue`; required when `origin` is `github`. Repository URLs and identifiers are not accepted. |
| `blueprint_id` | string | no | Predefined blueprint ID to apply (exclusive with `blueprint`) |
| `blueprint` | object | no | Inline blueprint JSON object to apply (exclusive with `blueprint_id`) |

**Returns**: Created project object with assigned `id`.

---

### GitHub repository selection for `project_create`

When creating a GitHub-origin project, keep the selection flow on the same authenticated MCP
connection:

1. Call `github_repository_selections_list`. Its redacted output supplies the selectable
   `full_name` values only.
2. Call `github_repository_selection_issue` with one returned `full_name`.
3. Pass its `selection_code` as `repository_selection_code` to `project_create`.

The code is bound to that caller, expires in five minutes, and is consumed once. The API resolves
the clone metadata from its server-side authorization; do not send repository URLs, numeric IDs,
installation IDs, permissions, tokens, or provider errors.

---

### `project_rename`

Rename a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `name` | string | yes | New name |

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
| `default_model_microsoft_foundry` | string | no | Model ID for the BYOK provider; the legacy field name remains supported. |

**Returns**: Confirmation message.

---

## Runs

### `run_submit`

Legacy compatibility alias that starts a coordinator run directly in `direct` mode. Prefer `run_task` for the common one-call flow, or `coordinator_start` for full manual control.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task` | string | yes | Task description for the agent |
| `agent_name` | string | no | Target team member name (e.g., `"ripley"`) |
| `base_branch` | string | no | Branch to base the run on (defaults to current) |
| `model_source` | string | no | Model provider override |

**Returns**: `{ run_id, status, start_mode }`.

---

### `run_task`

Run the common coordinator workflow in one call: start the run, poll until it reaches a terminal state or a gate, and return the artifacts or the next required action.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task` | string | yes | Goal or task statement for the coordinator |
| `workflow_id` | string | no | Workflow override. Must already be in the project's `allowed_workflow_ids`. |
| `model_id` | string | no | Coordinator model override |
| `start_mode` | string | no | `direct` (default) or `defineOutcome` |
| `timeout_seconds` | integer | no | Maximum wait before returning partial state (default `600`) |
| `poll_interval_seconds` | integer | no | Poll cadence while waiting (default `2`) |

**Returns** one of these shapes:

- Success:

```json
{
  "run_id": "abc123",
  "status": "merged",
  "artifacts": [{ "path": "README.md" }],
  "run": { "...": "run_status payload" }
}
```

- Outcome-spec gate:

```json
{
  "run_id": "abc123",
  "status": "awaiting_confirmation",
  "review_prompt": "Coordinator drafted an outcome spec. Call coordinator_outcome_spec_get ...",
  "run": { "...": "run_status payload" }
}
```

- Human review gate:

```json
{
  "run_id": "abc123",
  "status": "awaiting_review",
  "review_prompt": "Run is awaiting human review. Call run_review ...",
  "run": { "...": "run_status payload" }
}
```

- Timeout / partial state:

```json
{
  "run_id": "abc123",
  "status": "timed_out",
  "hint": "Call run_status for a quick snapshot or run_watch if you want to follow the live stream.",
  "run": { "...": "latest run_status payload" }
}
```

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

## GitHub App capabilities

Microsoft Entra remains the Agentweaver product identity. These tools connect only the
purpose-bound GitHub App capabilities. Browser handoffs and polling never return OAuth
state, callback cookies, credentials, repositories, installations, or permissions.

### `github_repo_app_connect`

Start the current human's Repo App authorization.

**Parameters**: none

**Returns**: `{ transaction_id, browser_url, expires_at }`. Open `browser_url` in a
browser and poll the returned transaction ID.

---

### `github_repo_app_authorization_status`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `transaction_id` | string | yes | Opaque ID returned by `github_repo_app_connect`. |

**Returns**: `{ status }`, where status is `pending`, `completed`, `failed`, or `expired`.

---

### `github_repo_app_disconnect`

Disconnect the current human's Repo App authorization and invalidate its outstanding
authorization transactions.

**Parameters**: none

**Returns**: `{ status: "disconnected" }`.

---

### `project_copilot_app_connect`

Start an Owner-authorized, project-pinned Copilot App connection.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project to bind. The backend derives current Owner authority. |

**Returns**: `{ transaction_id, browser_url, expires_at }`.

---

### `project_copilot_app_authorization_status`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project originally passed to `project_copilot_app_connect`. |
| `transaction_id` | string | yes | Opaque transaction ID. |

**Returns**: `{ status }`, where status is `pending`, `completed`, `failed`, or `expired`.

---

### `project_copilot_app_disconnect`

Disconnect a project Copilot binding. The API allows this de-privileging operation only
to a human Project Owner or human platform administrator.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project whose binding will be disconnected. |

**Returns**: `{ status: "disconnected" }`.

---

### `project_github_capability_status`

Get the server-derived, redacted unattended capability readiness for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project to inspect; requires current Project Owner authority. |

**Returns**: `{ status, reason_code, message, repo_app_installation_connected }`.

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

MCP exposes workflow triggers today in two ways:

- `workflows_list` and `workflow_get` surface the current `trigger` on each workflow, including
  event predicates when one is configured.
- `workflow_generate` can draft schedule or event triggers from natural-language descriptions, and
  `workflow_save` can persist trigger edits by saving the workflow YAML.

There is **no dedicated MCP tool** yet for the structured REST trigger CRUD surface
(`GET/PUT/DELETE /api/projects/{projectId}/workflows/{workflowId}/trigger`). In MCP, trigger writes
currently go through full-workflow YAML edits rather than a `workflow_set_trigger` /
`workflow_configure` helper.

### `workflows_list`

List all discovered workflow definitions for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Array of workflow summaries with `id`, `name`, validation state, effective-default
status, and `trigger` (when configured).

---

### `workflow_get`

Get the full definition of a single workflow by ID.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `workflow_id` | string | yes | Workflow ID |

**Returns**: Full workflow definition with `id`, `name`, `trigger`, `nodes`, and `edges`. For event
triggers, the returned predicate objects use the same structured shape as the REST API (`hasLabel`,
`baseBranch`, `commentMatches`, `or`, `not`, and so on).

---

### `workflows_sync`

Re-read the project's workflow definitions from disk, refreshing the in-memory registry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |

**Returns**: Updated workflow list (same shape as `workflows_list`).

---

### `workflow_generate`

Generate a new workflow YAML **draft** from a natural-language description. Nothing is written to disk — use `workflow_save` to persist (FR-065). Natural-language trigger inference covers both schedule prompts (“every Monday at 09:00 UTC”) and curated GitHub event prompts (“when someone comments `/agentweaver:triage`”).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `description` | string | yes | Plain-language description of the pipeline |

**Returns**: `{ yaml, workflow_id, was_corrected }` — the draft YAML, a suggested id (matching the `id` field), and whether a correction pass was applied.

---

### `workflow_save`

Persist a workflow YAML to the project workspace (`.agentweaver/workflows/`). Validates and dry-run binds every node before writing; on success the workflow is immediately coordinator-selectable. This is also the current MCP write path for trigger changes.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `workflow_id` | string | yes | Workflow ID (must match the `id` in the YAML) |
| `yaml` | string | yes | Workflow YAML to save |

**Returns**: The full `WorkflowDefinitionDto` (id, nodes, edges, trigger, validation status). Returns `400` on YAML parse errors, malformed trigger predicates, an unwired node type, or an `id`/route mismatch.

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
