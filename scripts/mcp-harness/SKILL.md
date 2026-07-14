# MCP harness CLI contract

Use this harness to validate Agentweaver's MCP protocol surface and capture
deterministic smoke evidence. It is appropriate for MCP end-to-end validation,
MCP tool-contract regression checks, and investigation of an MCP-reported issue.
It is not a REST or browser test harness.

## Run the implemented smoke path

Install the harness dependencies, run its unit tests, then invoke its existing
smoke entry point:

```powershell
npm --prefix scripts/mcp-harness test

npm --prefix scripts/mcp-harness run smoke -- `
  --target stdio `
  --server-command dotnet `
  --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' `
  --project-id <id>
```

List the built-in MCP scenario starters:

```powershell
node scripts/mcp-harness/smoke/mcp-cli-smoke.mjs --list
```

That prints JSON with the current reviewed persona IDs that have MCP adapters. The
implemented runner is still the smoke path above; for cross-surface scenario discovery
or generation, read `scripts/persona-briefs/SKILL.md`.

The smoke command supports `--target stdio|http`, `--token`, `--project-id`,
`--goal`, `--timeout-ms`, and `--poll-ms`. For a production target, provide
both `--allow-prod` and `--i-understand-prod`. Do not claim support for
persona, direct scenario execution, verdict, or judge CLI flags: those are not
implemented by the current smoke entry point.

`--target stdio` spawns a local subprocess (`--server-command`/`--server-args`)
and has no network target — the host allowlist in `target-guard.mjs` (and the
`--allow-prod`/`--i-understand-prod` requirement) only applies to the HTTP
transport, where `--target` is a real base URL that **must include the `/mcp`
path suffix** (e.g. `https://<host>/mcp`; the bare origin is not the endpoint).

HTTP transport also requires **OAuth**: the Agentweaver MCP server rejects
unauthenticated requests, so `--token`/`AGENTWEAVER_TOKEN` must be a valid
OAuth-derived bearer token (obtained via the app's own sign-in flow, or
`gh auth token` where that identity is trusted), not an arbitrary string.
Stdio transport has no such requirement.

On success, the command emits JSON headed by `DRIVE+CAPTURE OK`, including the
run ID, terminal status, artifact count, and compatibility report. A non-zero
exit means the driver or its capability contract failed; preserve its output
when reporting the failure.

## Discovery and contract rules

The harness begins every session with live MCP `tools/list` discovery. Treat
that response—the live tool names, schemas, and descriptions—as the sole
action menu. Do not hardcode or substitute tool names from documentation.

Independently, the smoke path checks
`scripts/mcp-harness/required-capabilities.json`. This regression contract
verifies the required workflow capabilities and their input/output schema
shapes, while allowing unrelated additive tools. A contract failure is a
surface regression, not a reason to bypass discovery or call a guessed tool.

Only provide a project ID for a safe, disposable test project. The smoke
workflow submits a task, polls it, retrieves artifacts, and attempts cleanup.
