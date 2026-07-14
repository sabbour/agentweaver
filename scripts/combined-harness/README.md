# Combined harness launcher

`launch.mjs` is a thin process orchestrator: it starts each selected harness as an
independent child process, then calls `scripts/harness-judge/meta-aggregate.mjs`.
It does not implement persona driving or judging.

Commands are JSON argv arrays, not shell strings. The launcher replaces
`{batchId}`, `{scenarioId}`, and `{verdictDir}` in every argument and also supplies
`AGENTWEAVER_BATCH_ID`, `AGENTWEAVER_SCENARIO_ID`, and
`AGENTWEAVER_VERDICT_DIR` to each child. This lets the existing surface CLIs retain
their own contracts while sharing the canonical join key.

```powershell
node scripts/combined-harness/launch.mjs `
  --scenario-id priya-ticket-triage `
  --api-command '["node","scripts/api-harness/run-persona.mjs","--scenario","{scenarioId}","--batch-id","{batchId}","--out","{verdictDir}/api.json"]' `
  --ui-command '["node","<your-ui-runner>","--batch-id","{batchId}","--scenario-id","{scenarioId}","--out","{verdictDir}/ui.json"]' `
  --mcp-command '["node","<your-mcp-runner>","--batch-id","{batchId}","--scenario-id","{scenarioId}","--out","{verdictDir}/mcp.json"]'
```

The API command defaults to `run-persona.mjs`; UI and MCP commands are explicit
because their current standalone CLIs require surface-specific session/target
configuration. A nonzero child exit, absent verdict, or aggregation failure is
recorded in `launcher-report.json`; completed sibling processes are still aggregated.
Use `--surfaces api,mcp` (or another subset) for a scoped pass.
