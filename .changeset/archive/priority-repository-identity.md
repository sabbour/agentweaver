---
"Agentweaver.Orchestrator": patch
"Agentweaver.Gateway": patch
"Agentweaver.Mcp": patch
---

Keep provider and repository identity in Priority issue and pull request requests,
storage and receipts. Expose the same fields in Gateway and generated MCP schemas.
Migration 033 preserves legacy rows and replaces number-only uniqueness with
separate identified and legacy rules. Optional migration 032 remains excluded.

The new owner verifies both complete schema 031 and complete schema 033. Before
033, repository link saves fail closed with database_update_pending; existing
operations remain available. Deploy compatible images first, then migrate.
Older owner images reject schema 033. Publish a machine-readable schema
compatibility contract alongside the source migration catalog.
