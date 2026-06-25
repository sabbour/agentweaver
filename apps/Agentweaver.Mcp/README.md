# Agentweaver MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Agentweaver project management capabilities to AI assistants such as GitHub Copilot.

## Configuration

| Environment variable     | Required | Description                          |
|--------------------------|----------|--------------------------------------|
| `AGENTWEAVER_API_URL`    | No       | Base URL of the Agentweaver API (default: `http://localhost:5000`) |
| `AGENTWEAVER_API_KEY`    | Yes      | Bearer token for the Agentweaver API |

## `.mcp.json` example

```json
{
  "mcpServers": {
    "agentweaver": {
      "command": "dotnet",
      "args": ["run", "--project", "apps/Agentweaver.Mcp", "--no-build"],
      "env": {
        "AGENTWEAVER_API_URL": "http://localhost:5000",
        "AGENTWEAVER_API_KEY": "<your-key>"
      }
    }
  }
}
```

## Available Tools

### Backlog

| Tool | Description |
|------|-------------|
| `backlog_capture_task` | Capture a new task into the project backlog. |
| `backlog_edit_task` | Edit the title and/or description of a backlog task. |
| `backlog_delete_task` | Delete a backlog task. |
| `backlog_move_to_ready` | Move a task from Backlog to Ready. |
| `backlog_move_to_backlog` | Move a task from Ready back to Backlog. |
| `backlog_reorder_task` | Reorder a task within its current bucket. |
| `backlog_get_board` | Get the full Kanban board for a project. |
| `backlog_archive_task` | Archive a backlog task off the active board. |
| `backlog_get_workflow_stages` | Get the ordered canonical run-bucket definitions. |
| `backlog_get_settings` | Get per-project backlog pickup settings. |
| `backlog_set_settings` | Set per-project backlog pickup settings. |
| `send_all_backlog_to_ready` | Bulk-promote all Backlog tasks to Ready. |
| `backlog_decompose_spec` | Decompose a workspace spec file into proposed backlog tasks. |

#### `backlog_decompose_spec`

Reads a markdown file from the project's workspace, runs AI decomposition, and returns proposed backlog items for review. Use `confirm=true` to create the tasks; `confirm=false` (default) for preview only. Results are capped at 50 items.

**Input**

| Parameter    | Type    | Required | Description |
|--------------|---------|----------|-------------|
| `project_id` | string  | Yes      | Project ID |
| `file_path`  | string  | Yes      | Relative path to a markdown file within the project workspace |
| `confirm`    | boolean | No       | `true` creates the tasks; `false` (default) returns preview only |

**Output**

```json
{
  "proposed_items": [
    {
      "title": "Implement login page",
      "description": "Build the user-facing login form with email/password fields.",
      "already_exists": false
    }
  ],
  "was_capped": false,
  "total_found": 5
}
```

- `was_capped` — `true` if the agent found more than 50 items and the list was truncated.
- `already_exists` — `true` for items that are already present in the project backlog (deduplication by title + source file).

**Errors**

| Condition | Message |
|-----------|---------|
| Project not found | `Project {id} not found` |
| File path invalid / outside sandbox | API's error message (HTTP 400) |
| API unreachable | `Agentweaver API unavailable` |

### Blueprints

| Tool | Description |
|------|-------------|
| `list_blueprints` | List predefined Agentweaver blueprints. |
| `blueprint_generate` | Generate a project blueprint from a natural language description of the team and goals. The agent can inspect before creating a project. |
| `validate_blueprint` | Validate a blueprint object against the schema. |

### Catalog

Tools for browsing the agent catalog.

### Coordinator

Tools for managing coordinator runs.

### Diagnostics

Diagnostic and health-check tools.

### GitHub Auth

GitHub OAuth tools.

### Memory

Project memory tools.

### Projects

Project CRUD tools.

### Runs

Run management tools.

### Sandbox Policy

Sandbox policy tools.

### Teams

Team management tools.

### Workflows

| Tool | Description |
|------|-------------|
| `workflows_list` | List all discovered workflow definitions for a project. |
| `workflow_get` | Get the full definition of a single workflow by ID. |
| `workflows_sync` | Re-read workflow definitions from disk, refreshing the registry. |
| `workflow_generate` | Generate a new workflow YAML draft from a natural language description. Not yet saved — use `workflow_save` to persist. |
| `workflow_save` | Save a workflow YAML to the project workspace. Validates and dry-run binds before saving. |

#### Agent Generate → Inspect → Save Loop (FR-065)

An Agentweaver `CopilotAIAgent` can author and persist a new workflow in three steps:

1. **`workflow_generate(project_id, description)`** — calls `POST /api/projects/{id}/workflows/generate` and returns a YAML draft plus a `workflow_id`. Nothing is written to disk yet.
2. **Inspect** — the agent reads the YAML in its own turn, reasons over the node graph, and optionally edits the YAML text before proceeding.
3. **`workflow_save(project_id, workflow_id, yaml)`** — calls `PUT /api/projects/{id}/workflows/{workflow_id}`, which validates, dry-run binds all nodes, writes the file, and returns the parsed `WorkflowDefinitionDto`. The workflow is now coordinator-selectable.

This design lets the agent catch schema or role mismatches before committing to disk, and keeps generation and persistence as separate, observable steps.

**`workflow_generate` output**

| Field | Type | Description |
|-------|------|-------------|
| `yaml` | string | Generated YAML draft |
| `workflow_id` | string | Suggested workflow id (matches the `id` field in the YAML) |
| `was_corrected` | boolean | `true` if the generator applied a correction pass (FR-060) |

**`workflow_save` output**

Returns the full `WorkflowDefinitionDto` JSON (id, nodes, edges, trigger, validation status) on success.

**Errors**

| Condition | Status | Message |
|-----------|--------|---------|
| Project not found | 404 | `Project {id} not found` |
| Description empty | 400 | `description is required` |
| YAML parse error | 400 | `YAML parse error at line N: …` |
| Node type not yet wired | 400 | Node classification error |
| `id` in YAML ≠ route id | 400 | Mismatch error with both ids |

### Workspace

| Tool | Description |
|------|-------------|
| Workspace file tree | Browse and read files within a project's workspace sandbox. |
