---
"Agentweaver.Abstractions": minor
"Agentweaver.EventsAndSessions": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.Projects.Config": minor
---

Add scoped spend budgets. `runLimits.spendBudgets` defines project, run, and agent budgets per meter source and unit, with soft and hard limits and a per-dispatch reserve. A project can add budgets and can only narrow platform budgets.
Orchestrator admits each native dispatch against committed Events spend plus the reserves of dispatches that Events hasn't accounted for. Hard budgets fail closed before send, soft limits record one notification per bucket and period, and migration `029_spend_budget_exposure.sql` stores only reservations and the observed Events accounting cursor.
Events spend evidence adds an optional agent total and witnesses dispatches from any run in the project. Projects adds the Orchestrator-only `GET /api/projects/{projectId}/runs/{runId}/spend-budgets` route for current project budgets.
Code comments and the spend budget design documentation explain how admission, reservations, and the accounting cursor work.
