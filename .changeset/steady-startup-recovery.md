---
"agentweaver": patch
---

Let API replicas answer liveness probes while interrupted runs recover. Postgres
replicas coordinate takeover and completed sweeps under an advisory lock; interrupted
sweeps honor shutdown without failing resumed runs. Readiness and OAuth requests stay
fail-closed until initial static-client reconciliation succeeds, while transient
reconciliation errors are retried without stopping the listener.
