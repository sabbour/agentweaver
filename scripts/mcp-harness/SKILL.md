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

The smoke command supports `--target stdio|http`, `--token`, `--project-id`,
`--goal`, `--timeout-ms`, and `--poll-ms`. For a production target, provide
both `--allow-prod` and `--i-understand-prod`. Do not claim support for
persona, scenario, verdict, or judge CLI flags: those are not implemented by
the current smoke entry point.

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
