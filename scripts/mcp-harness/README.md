# MCP harness

MCP-protocol-driven validation for Agentweaver. `tools/list` is discovered live at
session start and is the sole persona action menu. The independent
`required-capabilities.json` contract is a smoke/acceptance regression tripwire.

Run unit tests with `npm test` from this directory.

## Quickstart contract

- **stdio transport** (`--target stdio`) spawns a **local subprocess** via
  `--server-command`/`--server-args`. There is no network target involved, so
  `target-guard`'s host allowlist does **not** apply — `stdio` is only a
  transport-selector sentinel, never a URL.
- **http transport** (`--target <url>`) requires a real base URL, and that URL
  must include the server's `/mcp` path suffix (e.g. `https://<host>/mcp`) —
  the bare origin is not the endpoint. `target-guard`'s host allowlist
  (`localhost`, `127.0.0.1`, `::1`, `*.staging.*`) applies to it; production
  hosts require both `--allow-prod` and `--i-understand-prod`.
- The Agentweaver MCP server requires **OAuth**: connecting over http transport
  needs a valid, authenticated bearer token, not an arbitrary string.
  `transport-http.mjs` attaches `--token`/`AGENTWEAVER_TOKEN` as the request's
  `Authorization` header only when a token is supplied — an unauthenticated
  request will be rejected by the server. Obtain a token via the app's own
  OAuth sign-in flow (or `gh auth token` where that identity is what the
  Agentweaver server trusts); stdio transport has no such requirement since it
  never leaves the local subprocess.
- Required env vars / flags: `AGENTWEAVER_TOKEN` (or `--token`) for authenticated
  calls, and `--project-id` (project creation is scenario-owned — the smoke
  script does not create projects for you).

Stdio example:

```powershell
npm run smoke -- --target stdio --server-command dotnet --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' --project-id <id>
```

HTTP example (against a locally running server; the `/mcp` suffix is required,
and `$env:AGENTWEAVER_TOKEN` must be a valid OAuth-derived bearer token):

```powershell
npm run smoke -- --target http://localhost:5000/mcp --token $env:AGENTWEAVER_TOKEN --project-id <id>
```

List reviewed persona adapters without connecting to a server:

```powershell
node smoke/mcp-cli-smoke.mjs --list
```

`--list` prints the reviewed persona IDs that currently have MCP adapters. For
cross-surface scenario discovery and generation, read `../persona-briefs/SKILL.md`.
