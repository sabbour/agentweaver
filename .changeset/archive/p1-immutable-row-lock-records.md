---
"Agentweaver.EventsAndSessions": patch
"Agentweaver.Orchestrator": patch
---

Add forward migrations that reject all row updates to session identities,
provider bindings, fork lineage, coordinator decisions, and MAF checkpoints.
Inserts and row locks remain available. Startup checks the enabled,
unconditional update triggers. Earlier migration records and deletion rules
remain unchanged. PostgreSQL test cases compile; native execution and
deployment remain unverified.
