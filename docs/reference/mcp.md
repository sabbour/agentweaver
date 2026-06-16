# MCP server reference

The Agentweaver MCP server exposes all Agentweaver operations as structured tool calls over stdio. Any MCP-capable host (GitHub Copilot CLI, Claude, Cursor, Windsurf, etc.) can discover and invoke these tools automatically via the `.mcp.json` file at the repository root.

## Setup

Set the required environment variable before starting any MCP host that uses the server:

```
SCAFFOLDER_API_KEY=<your-api-key>
```

Optionally override the API base URL (defaults to `http://localhost:5000`):

```
SCAFFOLDER_API_URL=http://localhost:5000
```

The `.mcp.json` at the repository root registers the server automatically for MCP hosts that support auto-discovery (Copilot CLI ≥1.0.59 and equivalents). No manual registration is required beyond setting the environment variable.

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
| `repository_path` | string | yes | Absolute path to the local git repository |
| `model_source` | string | no | Model provider (`github-copilot`) |

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

Update the repository path for a project (e.g., after moving the repository on disk).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `repository_path` | string | yes | New absolute path to the repository |

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
| `model_source` | string | yes | Model provider (`github-copilot`) |
| `model` | string | no | Specific model name |

**Returns**: Updated project object.

---

## Runs

### `run_submit`

Submit a new agent run for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `task` | string | yes | Task description for the agent |
| `agent_name` | string | no | Target team member name (e.g., `"ripley"`) |
| `originating_branch` | string | no | Branch to base the run on (defaults to current) |
| `model_source` | string | no | Model provider override |

**Returns**: `{ run_id, status }`.

---

### `run_status`

Get the current status and details of a run.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `run_id` | string | yes | Run ID |

**Returns**: Run object with `status`, `task`, `agent_name`, `started_at`, `diff` (when in review), and outcome fields.

Possible `status` values: `pending`, `in_progress`, `awaiting_review`, `merging`, `merged`, `declined`, `failed`, `bounded`.

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

**Returns**: Proposal object (when `confirm=false`) or confirmed team object (when `confirm=true` or `confirm_proposal_id` is set).

---

### `team_member_add`

Add a new member to a project team.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `name` | string | yes | Member name (cast name, lowercase) |
| `role` | string | yes | Role description |
| `model` | string | no | Preferred model for this member |

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

**Parameters**: none (policy is resolved from the project's repository)

**Returns**: Current sandbox policy object.

---

### `sandbox_policy_set`

Update the sandbox policy.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `policy` | object | yes | Policy object to apply |

**Returns**: Updated sandbox policy.

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

### `inbox_submit`

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

### `inbox_list`

List inbox entries for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent` | string | no | Filter by agent name |
| `type` | string | no | Filter by entry type |
| `status` | string | no | `pending` (default) \| `merged` \| `rejected` |

**Returns**: Array of inbox entries.

---

### `inbox_merge`

Merge a pending inbox entry into team decisions.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `entry_id` | string | yes | Inbox entry ID |

**Returns**: Resulting decision object.

---

### `inbox_reject`

Reject a pending inbox entry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `entry_id` | string | yes | Inbox entry ID |

**Returns**: `"rejected"`.

---

### `decision_create`

Create a team decision directly (coordinator / Scribe path).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_id` | string | yes | Project ID |
| `agent_name` | string | yes | Agent recording the decision |
| `type` | string | yes | `architectural` \| `process` \| `scope` \| `technical` |
| `title` | string | yes | Short title |
| `content` | string | yes | Full content |
| `rationale` | string | no | Optional rationale |

**Returns**: Created decision object.

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

### `memory_add`

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

