---
name: agentweaver-harness
description: Run Agentweaver's complete cross-surface persona harness sweep (API, UI, and MCP), collect independently produced verdicts, and create a single batch/scenario rollup. Use for a full persona validation, cross-surface regression pass, or self-improvement pass; use the individual harness skills for one-surface diagnosis.
domain: testing
confidence: high
source: scripts/combined-harness/launch.mjs
allowed-tools: Bash(node scripts/combined-harness/launch.mjs:*)
---

# Combined Agentweaver harness

Use this skill for a full cross-surface sweep. It starts the selected API, UI, and
MCP harness commands as independent parallel processes, assigns one `batchId`, and
then runs the shared meta-aggregator. Findings correlate only on
`(batchId, scenarioId)`, never `runId`.

Read [`scripts/combined-harness/README.md`](../../../scripts/combined-harness/README.md)
before invoking it. Supply each surface command as a JSON argv array and use
`{batchId}`, `{scenarioId}`, and `{verdictDir}` placeholders so every harness writes
its canonical verdict into the isolated verdict directory.

Use `api-harness`, `ui-harness`, or `mcp-harness` instead for targeted investigation
of one surface. This launcher does not recreate any surface driver or judging logic.
