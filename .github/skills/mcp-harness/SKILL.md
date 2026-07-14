---
name: mcp-harness
description: Run Agentweaver's MCP protocol smoke harness for end-to-end MCP tool-surface validation, contract-regression checks, or investigation of MCP-reported issues. Use this whenever a user asks to test an MCP tool flow or validate the Agentweaver MCP integration; use the real harness CLI rather than recreating MCP calls manually.
domain: testing
confidence: high
source: scripts/mcp-harness/SKILL.md
---

# MCP harness

Use for MCP end-to-end validation, MCP tool-contract regression checks, and
investigating issues reported through the Agentweaver MCP surface. Follow the
detailed contract in `scripts/mcp-harness/SKILL.md`; run its implemented CLI
instead of manually recreating its discovery, capability checks, or smoke flow.
