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

Events migration 009 adds the project and time usage index. Orchestrator
migration 022 must be applied first. Existing usage ledger read grants are
sufficient. Cross-project overview is out of scope.

Record Events versions 001 through 009 in the source-only PostgreSQL plan
and initializer checks. Preserve the existing named Projects authorization
HTTP transport for both run-bound and project-scoped readers.
