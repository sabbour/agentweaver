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

Tools for managing project blueprints (see `BlueprintTools.cs`).

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

Workflow definition tools.

### Workspace

| Tool | Description |
|------|-------------|
| Workspace file tree | Browse and read files within a project's workspace sandbox. |
