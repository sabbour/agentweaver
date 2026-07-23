---
title: MCP CLI operator guide
---

# MCP CLI operator guide

Use this guide when driving Agentweaver from Copilot CLI, Claude Desktop, or another MCP client.

## Connecting a client

The hosted MCP endpoint is `https://<your-agentweaver-host>/mcp`. In the web app,
open **Account settings → MCP clients** to copy the URL and a client-specific configuration.

The MCP server accepts the same bearer-token model as Agentweaver's web/API session.
Agentweaver intentionally does not render the browser session token in the UI. Use an
existing GitHub bearer token instead—for example, obtain the token used by GitHub CLI
with `gh auth token`—or let a client that supports MCP OAuth complete its interactive
sign-in flow. Set it in your client environment as `AGENTWEAVER_TOKEN`; do not commit it
to a configuration file.

### Claude Desktop

Add this to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "agentweaver": {
      "url": "https://<your-agentweaver-host>/mcp",
      "headers": {
        "Authorization": "Bearer ${AGENTWEAVER_TOKEN}"
      }
    }
  }
}
```

### VS Code

Add this to your `mcp.json`:

```json
{
  "servers": {
    "agentweaver": {
      "type": "http",
      "url": "https://<your-agentweaver-host>/mcp",
      "headers": {
        "Authorization": "Bearer ${input:agentweaver-token}"
      }
    }
  },
  "inputs": [
    {
      "id": "agentweaver-token",
      "type": "promptString",
      "description": "Your existing Agentweaver/GitHub bearer token",
      "password": true
    }
  ]
}
```

### GitHub Copilot CLI

Add this to `.copilot/mcp-config.json` (or `~/.copilot/mcp-config.json`):

```json
{
  "mcpServers": {
    "agentweaver": {
      "type": "http",
      "url": "https://<your-agentweaver-host>/mcp",
      "headers": {
        "Authorization": "Bearer ${AGENTWEAVER_TOKEN}"
      }
    }
  }
}
```

## Auth-first

Before repo-backed or run-backed work, verify auth:

`github_status → [if signed out] github_signin → session_start`

If a call fails with `401`, do not show the raw error. Re-check auth, sign in, then retry.

## Recommended entry points

- **Common case:** `run_task` — one call that starts the run, polls, and returns artifacts or the next action.
- **Manual control:** `coordinator_start` → `run_status` / `run_watch` → `run_show_artifacts` → `run_get_file` → `run_review`
- **Legacy only:** `run_submit` is a compatibility alias; prefer the two options above.

## Poll vs. stream

- Use `run_status` for quick snapshots.
- Use `run_watch` only when the operator explicitly wants a live stream.
- Tell the operator that `run_watch` blocks while waiting; that is expected, not a hang.

## Timeout and retry protocol

If a tool returns `-32001 Request timed out`:

1. Call `diagnostics_get` (or `heartbeat_status`).
2. If the server looks healthy, retry **once** with brief backoff.
3. Safe-to-retry tools are read-only calls such as `run_status`, `coordinator_work_plan_get`, `coordinator_children_get`, `run_show_artifacts`, and `run_get_file`.
4. Do **not** blindly retry non-idempotent calls such as `coordinator_start`, `run_task`, `run_review`, `project_create`, or `project_delete` until you verify whether the first attempt already took effect.

## Full workflow sequence

Manual end-to-end path:

`github_signin → session_start → project_list (or project_create) → list_blueprints → coordinator_start → run_status (poll) → [coordinator_steer if needed] → [run_show_artifacts → run_get_file → run_review if gated]`

Common one-call path:

`github_signin → session_start → run_task`

## Backlog flow

`backlog_capture_task → backlog_move_to_ready` (or `send_all_backlog_to_ready`) `→ run_task`

Use `coordinator_start` instead of `run_task` when the operator wants manual control over the orchestration.

## Results retrieval

Always call `run_show_artifacts` before `run_get_file`. The artifact list tells you which file paths are valid inputs for `run_get_file`.

## Testing the MCP path

Run the deterministic CLI-to-MCP smoke test from the repository root:

```powershell
$env:AGENTWEAVER_BASE_URL = "https://<staging-host>"
$env:GITHUB_TOKEN = gh auth token
$env:AGENTWEAVER_SMOKE_PROJECT_ID = "<configured-staging-project-id>" # optional
npm run test:mcp-smoke
```

The test discovers the live MCP tools, verifies their capability contract, checks
GitHub sign-in, creates or reuses a project, submits a minimal run, polls for at
most five minutes, confirms an outcome gate when needed, requires a successful
terminal state and at least one artifact, then archives the run. Each failure
names the workflow step and MCP tool that failed.

For local stdio testing, pass the server command explicitly:

```powershell
npm run test:mcp-smoke -- --target stdio --server-command dotnet `
  --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' `
  --project-id <id>
```

HTTP targets must be localhost or staging unless both `--allow-prod` and
`--i-understand-prod` are supplied. Prefer a preconfigured smoke project; if no
project exists, use `AGENTWEAVER_SMOKE_PROJECT_NAME`,
`AGENTWEAVER_SMOKE_WORKING_DIRECTORY`, and `AGENTWEAVER_SMOKE_BLUEPRINT_ID` to
control project creation.
