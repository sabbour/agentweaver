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
- **http transport** (`--target <url>`) requires a real base URL. `target-guard`'s
  host allowlist (`localhost`, `127.0.0.1`, `::1`, `*.staging.*`) applies to it;
  production hosts require both `--allow-prod` and `--i-understand-prod`.
- Required env vars / flags: `AGENTWEAVER_TOKEN` (or `--token`) for authenticated
  calls, and `--project-id` (project creation is scenario-owned — the smoke
  script does not create projects for you).

Stdio example:

```powershell
npm run smoke -- --target stdio --server-command dotnet --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' --project-id <id>
```

HTTP example:

```powershell
npm run smoke -- --target http://localhost:5000/mcp --token $env:AGENTWEAVER_TOKEN --project-id <id>
```

List reviewed persona adapters without connecting to a server:

```powershell
node smoke/mcp-cli-smoke.mjs --list
```

`--list` prints the reviewed persona IDs that currently have MCP adapters. For
cross-surface scenario discovery and generation, read `../persona-briefs/SKILL.md`.
