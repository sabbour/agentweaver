---
title: MCP CLI operator guide
---

# MCP CLI operator guide

Use this guide when driving Agentweaver from Copilot CLI, Claude Desktop, or another MCP client.

## Connecting a client

The hosted MCP endpoint is `https://<your-agentweaver-host>/mcp`. In the web app,
open **Account settings → MCP clients** to copy the URL and a client-specific configuration.

The MCP server accepts only Agentweaver-issued broker access tokens for the exact
`https://<your-agentweaver-host>/mcp` resource and `mcp:invoke` scope. Microsoft Entra remains
the upstream product sign-in boundary; its access tokens are not MCP credentials. Do not commit
broker tokens to a configuration file.

### Local (stdio)

For a locally launched server (`dotnet run --project apps/Agentweaver.Mcp -- --stdio`, which is what
Copilot CLI does via the workspace `.mcp.json`), there is no interactive OAuth handshake and no
inbound HTTP request to carry your identity. Provide an Agentweaver broker token:

```jsonc
{
  "mcpServers": {
    "agentweaver": {
      "command": "dotnet",
      "args": ["run", "--project", "apps/Agentweaver.Mcp", "--no-build"],
      "env": {
        "AGENTWEAVER_API_URL": "http://localhost:5000",
        // Agentweaver broker token for the MCP resource and mcp:invoke scope.
        "AGENTWEAVER_TOKEN": "${input:agentweaver-token}"
      }
    }
  }
}
```

::: danger Broker tokens only
Raw Entra access tokens, GitHub tokens, and API keys are rejected. Stdio mode refuses to start
without `AGENTWEAVER_TOKEN`; the API independently validates the configured broker token and
applies project authorization on every tool call.
:::

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
      "description": "Your authenticated Agentweaver caller bearer",
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

## Sign-in and GitHub capabilities

Sign in to Agentweaver as a human Entra subject before using MCP. Authorize each GitHub capability separately:

`github_repo_app_connect → open browser_url → github_repo_app_authorization_status`

For unattended project work, a Project Owner also completes
`project_copilot_app_connect → open browser_url → project_copilot_app_authorization_status`
and verifies `project_github_capability_status`. Handoff and polling return only opaque
transaction identifiers and lifecycle state; credentials, OAuth state, installations,
repositories, and permissions never appear in MCP output.

If a call fails with `401`, do not show the raw error. Sign in to Agentweaver, then retry.

## Recommended entry points

- **Common case:** `run_task` — one call that starts the run, polls, and returns artifacts or the next action.
- **Manual control:** `coordinator_start` → `run_status` / `run_watch` → `run_show_artifacts` → `run_get_file` → `run_review`

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

`project_list (or project_create) → list_blueprints → coordinator_start → run_status (poll) → [coordinator_steer if needed] → [run_show_artifacts → run_get_file → run_review if gated]`

Common one-call path:

`run_task`

## Backlog flow

`backlog_capture_task → backlog_move_to_ready` (or `send_all_backlog_to_ready`) `→ run_task`

Use `coordinator_start` instead of `run_task` when the operator wants manual control over the orchestration.

## Results retrieval

Always call `run_show_artifacts` before `run_get_file`. The artifact list tells you which file paths are valid inputs for `run_get_file`.

## Testing the MCP path

Run the deterministic CLI-to-MCP smoke test from the repository root:

```powershell
$env:AGENTWEAVER_BASE_URL = "https://<staging-host>"
$env:AGENTWEAVER_TOKEN = "<agentweaver-broker-token>"
npm run test:mcp-smoke
```

The test discovers the live MCP tools (including `project_delete`), verifies their
capability contract, creates a uniquely named project using a server-assigned workspace
path and the software-development blueprint, submits a minimal run, polls for at most
five minutes, confirms an outcome gate when needed, and requires an artifact. In
`finally`, it archives the run and deletes only the project it created, including after
failure, timeout, or cancellation. The primary failure remains separate from cleanup
failures.

For local stdio testing, pass the server command explicitly:

```powershell
npm run test:mcp-smoke -- --target stdio --server-command dotnet `
  --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' `
  --project-id <id> `
  --project-is-disposable
```

An explicit project ID is accepted only with `--project-is-disposable`; smoke archives
its run but never deletes a caller-owned project. Without an ID, use
`AGENTWEAVER_SMOKE_PROJECT_NAME`, `AGENTWEAVER_SMOKE_WORKING_DIRECTORY`, and
`AGENTWEAVER_SMOKE_BLUEPRINT_ID` only to override creation defaults. Do not pass a local
Windows path to a deployed AKS target.

HTTP targets must be absolute URLs with pathname exactly `/mcp`. Any HTTPS host is
accepted; HTTP is loopback-only. URL credentials, fragments, `/mcp/`, and TLS bypasses
are rejected. Sanitized preflight evidence records origin/path, auth source, project/run
IDs, cleanup intent/result, and TLS mode without recording the token.
