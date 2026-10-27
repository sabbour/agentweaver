---
"Agentweaver.Identity": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.Persistence.Postgres": patch
---

Add append-only Orchestrator evidence for numeric-limit denials and expose explicit blocked replacement comparisons pending Environment and Host/Events provenance. Part of #1855.

Synchronize the embedded PostgreSQL runtime plan with migration 039 and its SELECT/INSERT-only evidence ledger.
