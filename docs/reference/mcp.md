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
