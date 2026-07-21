---
name: agentweaver-mcp-harness
description: Run Agentweaver's MCP protocol harness for tool-surface validation, repro reruns, or plain-English scenario exploration. Use for MCP flows/issues; use combined harness for cross-surface sweeps.
domain: testing
confidence: high
source: scripts/mcp-harness/SKILL.md
allowed-tools: Bash(node scripts/mcp-harness/smoke/mcp-cli-smoke.mjs:*) Bash(npm --prefix scripts/mcp-harness:*)
---

# MCP harness

Use for MCP end-to-end validation, MCP tool-contract regression checks, and
investigating issues reported through the Agentweaver MCP surface. Follow the
detailed contract in `scripts/mcp-harness/SKILL.md`; run its implemented CLI
instead of manually recreating its discovery, capability checks, or smoke flow.

That contract now includes `--list` for the reviewed built-in MCP catalog. Use the
separate `harness-scenarios` skill for cross-surface cataloging and persona generation.

Before running, check `scripts/harness-shared/learnings.md` (surface: `mcp` or `all`)
for already-known bugs, environment facts, and scenario-design notes so they aren't
rediscovered from source each run.
