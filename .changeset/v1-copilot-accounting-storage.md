---
"Agentweaver.Abstractions": minor
"Agentweaver.Providers": minor
"Agentweaver.EventsAndSessions": minor
---

Add immutable Cost bindings, versioned Copilot AI-credit pricing, and append-only
PostgreSQL usage storage with exact per-run and per-agent totals. Add migration
004 without changing existing journal, message, or project-fact history.

This source includes storage and pricing primitives only. It does not authorize
an SDK producer, expose a usage-writing route, or complete native usage ingestion.
