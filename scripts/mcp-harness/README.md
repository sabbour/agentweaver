# MCP harness

MCP-protocol-driven validation for Agentweaver. `tools/list` is discovered live at
session start and is the sole persona action menu. The independent
`required-capabilities.json` contract is a smoke/acceptance regression tripwire.

Run unit tests with `npm test` from this directory.

There are two entry points:

- `run-persona.mjs` — full **dynamic** persona scenario validation. No fixed script:
  a fresh sub-agent dispatched under `agent-driver/AGENT.md` (the MCP peer of the API
  harness's `PersonaActor`) discovers the live tool menu, decides each `tools/call`
  live, pushes back, never blind-approves a gate, and appends its own JSONL transcript.
  `run-persona.mjs` owns the deterministic scaffolding (target guard, transcript path,
  capability contract, judge) in two phases — `prepare` (emit the dispatch prompt) and
  `finalize --transcript <path>` (judge the transcript into a normalized verdict). See
  `SKILL.md` for the full contract.
- `smoke/mcp-cli-smoke.mjs` — a fast, fixed connectivity + capability tripwire.

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
  request will be rejected by the server. Obtain an Agentweaver broker token via
  the app's OAuth flow; raw Entra and GitHub tokens are rejected. Stdio transport
  passes its configured broker token only to downstream API calls.
- Authentication: `AGENTWEAVER_TOKEN` or `--token`. The smoke
  assumes GitHub capability authorization is supplied out of band; it does not
  automate a one-time browser handoff.
- Project selection: `--project-id` / `AGENTWEAVER_SMOKE_PROJECT_ID` takes
  precedence. Otherwise the smoke reuses `--project-name` /
  `AGENTWEAVER_SMOKE_PROJECT_NAME`, falls back to the first existing project,
  or creates a project with `project_create`. Use `--working-directory` and
  `--blueprint-id` (or their `AGENTWEAVER_SMOKE_*` env equivalents) to
  configure project creation.

Stdio example:

```powershell
npm run smoke -- --target stdio --server-command dotnet --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' --project-id <id>
```

HTTP example (against a locally running server; the `/mcp` suffix is required,
and `$env:AGENTWEAVER_TOKEN` must be a valid OAuth-derived bearer token):

```powershell
npm run smoke -- --target http://localhost:5000/mcp --token $env:AGENTWEAVER_TOKEN --project-id <id>
```

From the repository root, the equivalent command is `npm run test:mcp-smoke -- ...`.
When `AGENTWEAVER_BASE_URL` is set, the root command automatically targets its
`/mcp` endpoint.

List reviewed persona adapters without connecting to a server:

```powershell
node smoke/mcp-cli-smoke.mjs --list
```

`--list` prints the reviewed persona IDs that currently have MCP adapters. For
cross-surface scenario discovery and generation, read `../persona-briefs/SKILL.md`.
