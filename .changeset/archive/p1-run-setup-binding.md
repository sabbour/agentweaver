---
"Agentweaver.Abstractions": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.Projects.Config": minor
"Agentweaver.Gateway": patch
---

Bind authenticated P1 setup receipts to new run requests and atomically accept
the root, initial decision and Core invocation binding. Add a narrow run-bound
Projects verifier and a current binding read for later consumers, while keeping
the existing two-stage flow and documenting the cross-service revocation race.
