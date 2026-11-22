---
"Agentweaver.Abstractions": minor
"Agentweaver.Gateway": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.Environment": minor
---

Add the read-only `GET /api/v1/runs/{runId}/egress` API across Gateway, Orchestrator, and Environment. Orchestrator resolves a single visible run in the selected tenant, forwards the caller bearer and tenant selector to Environment, and Environment revalidates current Projects authority before querying attributed egress flows with keyset pagination and `Cache-Control: no-store`.
