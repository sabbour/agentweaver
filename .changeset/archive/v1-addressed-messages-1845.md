---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.Persistence.Postgres": patch
---

Add provider-neutral addressed-message contracts, PostgreSQL delivery persistence, and the versioned Orchestrator service host for owner-validated coordination. The owner validates current Projects authority and its durable message outbox, while Events & Sessions registers sessions, admits fenced messages, and returns delivery at explicit turn boundaries. Receipt acknowledgment does not approve gates or complete work; no background relay or automatic AgentHost scheduler is included.
