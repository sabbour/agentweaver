---
"agentweaver": patch
---

Retain output file bytes and diffs in immutable generation-fenced revisions alongside terminal publication, and admit confirmed no-change receipts as dependency identities. Expose revision history, exact file retrieval, and comparison through REST, MCP, and the run UI. Backlog pickup now composes claimed revisions in dependency order, rejects ambiguous conflicts or unavailable content, persists a deterministic execution-input identity, and launches or recovers the run from the exact materialized commit without changing its publication branch.
