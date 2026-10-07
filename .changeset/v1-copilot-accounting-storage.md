---
"Agentweaver.Abstractions": minor
"Agentweaver.Providers": minor
"Agentweaver.EventsAndSessions": minor
---

Add immutable Cost bindings, versioned Copilot AI-credit pricing, and append-only
PostgreSQL usage storage with exact per-run and per-agent totals. Persist canonical
SHA-256 hashes and return immutable accounting receipts only after commit. Add migration
004 without changing existing journal, message, or project-fact history.

This source includes storage and pricing primitives only. It does not authorize
an SDK producer, expose a usage-writing route, or complete native usage ingestion.
