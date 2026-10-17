---
"Agentweaver.Orchestrator": patch
---

Record runtime tool-invocation reservations on a run-level ledger stream instead of the
session's message stream, so a reserved tool call no longer blocks later addressed
messages from that session from being admitted.
