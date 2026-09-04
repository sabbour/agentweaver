---
title: Connect an MCP client
---

# Connect an MCP client

Connect GitHub Copilot CLI, GitHub Copilot desktop, VS Code, or Claude Desktop
to Agentweaver through the hosted MCP endpoint.

## Before you connect

- Use a current client version that supports remote HTTP MCP servers and OAuth.
- Sign in to the Agentweaver web app. On deployments that use Microsoft Entra
  ID, complete the Entra sign-in before authorizing the client.
- In Agentweaver, open **Account settings → MCP clients** and copy the displayed
  URL. For a hosted deployment, the exact form is
  `https://<deployment-origin>/mcp`.

The `/mcp` path is required. Do not add a trailing slash, URL credentials,
authorization headers, or query parameters.

## How authorization works

Configure only the MCP URL for normal hosted use. On connection, the client:

1. discovers the anonymous OAuth protected-resource metadata for `/mcp`;
2. discovers Agentweaver's same-origin authorization server;
3. opens a browser for Agentweaver sign-in when required;
4. asks you to approve the least-privilege `mcp:invoke` scope; and
5. completes authorization code + PKCE S256 and manages token refresh.

You do not copy or paste an Entra token, broker access token, API key, or other
credential. The MCP client completes OAuth and keeps credentials out of URLs,
commands, and checked-in configuration.

## Install the Agentweaver Driver

GitHub Copilot clients work best with the **Agentweaver Driver** custom agent.
Its definition includes the current MCP tool map and the safe playbooks for
discovery, confirmation, run supervision, review, retries, and credential
handling.

Every hosted Agentweaver deployment serves the definition without
authentication from the same origin:

`https://<deployment-origin>/agents/agentweaver.agent.md`

For example, if the MCP URL is `https://agentweaver.example.com/mcp`, the agent
definition is
`https://agentweaver.example.com/agents/agentweaver.agent.md`.

For Copilot CLI, save it as a user-level agent:

::: code-group

```powershell [Windows PowerShell]
$agentDirectory = Join-Path $HOME ".copilot\agents"
New-Item -ItemType Directory -Force $agentDirectory | Out-Null
Invoke-WebRequest `
  -Uri "https://<deployment-origin>/agents/agentweaver.agent.md" `
  -OutFile (Join-Path $agentDirectory "agentweaver.agent.md")
```

```shell [macOS and Linux]
install -d "$HOME/.copilot/agents"
curl --fail --proto '=https' --tlsv1.2 \
  --output "$HOME/.copilot/agents/agentweaver.agent.md" \
  "https://<deployment-origin>/agents/agentweaver.agent.md"
```

:::

Review the downloaded definition, restart Copilot CLI, run `/agent`, and select
`agentweaver`. You can also invoke it directly with
`copilot --agent=agentweaver --prompt "<your Agentweaver task>"`.

For VS Code or GitHub Copilot desktop, place the same file at
`.github/agents/agentweaver.agent.md` in the repository where you will work,
then select **Agentweaver Driver** from the agent picker. Agentweaver-created
projects already receive this repository-level definition, so no separate
install is needed there.

## GitHub Copilot CLI

1. Start an interactive Copilot CLI session and run `/mcp add`.
2. Name the server `agentweaver`, choose **HTTP**, enter the MCP server URL,
   leave HTTP headers empty, and save.
3. Complete the browser sign-in and consent flow.
4. Run `/mcp show agentweaver` and confirm that the server is connected and its
   tools are listed.

The non-interactive equivalent is:

```shell
copilot mcp add --transport http agentweaver https://<deployment-origin>/mcp
```

Do not add `--header` or put credentials in the command.

## GitHub Copilot desktop

1. Open **Customize → MCP servers**.
2. Add a custom remote HTTP server named **Agentweaver** and enter the MCP
   server URL.
3. Connect and complete the browser sign-in and consent flow.
4. Start a session and confirm that Agentweaver tools appear in the tool picker.

GitHub manages this configuration surface, and labels can move between desktop
releases. If the placement changes, search the **Customize** view for MCP
servers; do not replace OAuth with a copied token. Select **Agentweaver Driver**
from the agent picker after the repository-level definition is installed.

## VS Code

1. Open the Command Palette and run **MCP: Add Server**.
2. Choose **HTTP**, enter the MCP server URL, and select the user or workspace
   configuration scope.
3. Start the server and complete the browser sign-in and consent flow.
4. Run **MCP: List Servers** and confirm that Agentweaver is running and exposes
   tools.
5. Select **Agentweaver Driver** from the Copilot Chat agent picker after the
   repository-level definition is installed.

VS Code writes its client-managed `mcp.json` entry. No input variable or
authorization header is required for Agentweaver OAuth.

## Claude Desktop

1. Open **Customize → Connectors**.
2. Add a custom connector named **Agentweaver**.
3. Enter the MCP server URL from Agentweaver Account settings.
4. Open **Advanced settings** and enter `agentweaver-claude` as the
   **OAuth Client ID**. Leave **OAuth Client Secret** empty.
5. Connect, complete browser sign-in and consent, then confirm that the
   connector lists Agentweaver tools.

Agentweaver registers `agentweaver-claude` as a public OAuth client for only
Claude's exact hosted callback,
`https://claude.ai/api/mcp/auth_callback`. Claude sends S256 PKCE on the
authorization request, so no client secret, static authorization header, or
manually copied token is used. URL-only setup is not supported because
Agentweaver intentionally rejects HTTPS callbacks submitted through anonymous
dynamic client registration.

## Local repository development only

The hosted flow above is the supported onboarding path. A repository developer
who intentionally launches `apps/Agentweaver.Mcp` over stdio does not have an
HTTP browser callback and must supply an Agentweaver broker token through the
process environment. This is for trusted local development and deterministic
test harnesses only, not hosted client setup. Never place the token in command
arguments, source control, or a URL. Raw Entra access tokens, GitHub tokens, and
API keys are rejected.

## GitHub capabilities after MCP sign-in

MCP sign-in authorizes Agentweaver tool calls. Repository and AI access are
separate GitHub capabilities. Authorize each one only when a workflow needs it:

`github_repo_app_connect → open browser_url → github_repo_app_authorization_status`

For unattended project work, a Project Owner also completes
`project_copilot_app_connect → open browser_url → project_copilot_app_authorization_status`
and verifies `project_github_capability_status`. Handoff and polling return only opaque
transaction identifiers and lifecycle state; credentials, OAuth state, installations,
repositories, and permissions never appear in MCP output.

If the client reports `401`, reconnect the MCP server and complete the browser
OAuth flow. Do not work around the failure by pasting a token into client
settings.

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

This section is for repository maintainers testing a deployment, not for client
onboarding. Put the short-lived broker token in the process environment, never
in command arguments or source control.

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
