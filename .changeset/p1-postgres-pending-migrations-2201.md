---
"Agentweaver.Orchestrator": patch
"Agentweaver.EventsAndSessions": patch
---

Add read-only inspection and bounded pending-migration modes to the P1 PostgreSQL initializer for already initialized databases ([#2201](https://github.com/sabbour/agentweaver/issues/2201)). The Orchestrator and Events & Sessions migrators expose their supported version catalogs; migration behavior is unchanged.
