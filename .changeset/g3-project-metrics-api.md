---
"Agentweaver.Abstractions": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.EventsAndSessions": minor
"Agentweaver.Gateway": minor
"Agentweaver.Web": minor
---

Add project-scoped metrics and usage read contracts, fresh read authorization,
bounded UTC intervals, strict Gateway/OpenAPI discovery, and browser types.
Keep persisted cost units and dispositions separate and unknown usage nullable.

Events migration 009 adds the project/time usage index. Applying it to a live
database requires separate approval; G2's Orchestrator migration 022 is also
a prerequisite. Existing usage ledger read grants are sufficient. Cross-project
overview is out of scope.
