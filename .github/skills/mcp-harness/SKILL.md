---
name: mcp-harness
description: Run Agentweaver's MCP protocol harness for tool-surface validation, repro reruns, or plain-English scenario exploration. Use for MCP flows/issues; use combined harness for cross-surface sweeps.
domain: testing
confidence: high
source: scripts/mcp-harness/SKILL.md
---

# MCP harness

Use for MCP end-to-end validation, MCP tool-contract regression checks, and
investigating issues reported through the Agentweaver MCP surface. Follow the
detailed contract in `scripts/mcp-harness/SKILL.md`; run its implemented CLI
instead of manually recreating its discovery, capability checks, or smoke flow.
