---
name: agentweaver-harness-scenarios
description: List built-in Agentweaver harness scenarios/persona catalogs by surface, or generate a reviewed new persona core and surface adapter for API, UI, or MCP.
domain: testing
confidence: high
source: scripts/persona-briefs/SKILL.md
allowed-tools: Bash(node scripts/api-harness/run-persona.mjs:*) Bash(node scripts/ui-harness/agent-driver-ui/tools.mjs:*) Bash(node scripts/mcp-harness/smoke/mcp-cli-smoke.mjs:*) Bash(node scripts/persona-briefs/find-similar.mjs:*) Bash(node scripts/persona-briefs/generate-core.mjs:*) Bash(node scripts/persona-briefs/generate-adapter.mjs:*)
---

# Harness scenario catalog and generation

Read [`scripts/persona-briefs/SKILL.md`](../../../scripts/persona-briefs/SKILL.md)
before using this skill. It is the source of truth for:

- listing the current built-in scenario/persona catalog for API, UI, and MCP
- checking `scripts/persona-briefs/catalog.json` for a close match via
  `find-similar.mjs` before generating anything new
- generating a new reviewed persona core plus a reviewed surface adapter
- review and safety constraints for generated deep scenarios

Use this skill when you need scenario discovery or authoring. Use `agentweaver-api-harness`,
`agentweaver-ui-harness`, `agentweaver-mcp-harness`, or `agentweaver-harness` when you are ready to execute
the actual run.
