---
"Agentweaver.Orchestrator.Core": minor
"Agentweaver.Orchestrator": minor
---

Promote a coordinator run's confirmed work plan into project backlog tasks
before execution starts. Each promoted task records its origin: the promotion,
run, root session, work plan, and work item. Retries with the same idempotency
key return the stored promotion, and dispatch rejects a promoted plan. Tasks
captured directly have no origin.
