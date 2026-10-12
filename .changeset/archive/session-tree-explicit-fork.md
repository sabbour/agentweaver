---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": minor
"Agentweaver.Orchestrator": minor
---

Add owner-admitted, fenced, idempotent session forks that register verified Events journal lineage without extending object retention. Persist failed and indeterminate run outcomes with cause/reference/version, advance fences during failure and recovery, and record both transitions in the owner outbox without claiming physical SDK effects.
