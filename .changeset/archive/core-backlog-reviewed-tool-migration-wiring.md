---
"Agentweaver.Orchestrator": patch
---

Include migrations 016 and 018 in the existing Core migration chain.
Require the backlog and reviewed remote-tool snapshot tables before normal startup.
Verify the snapshot protection trigger and required columns through PostgreSQL system tables.
Insert the newly created backlog task by its exact task ID, not its position in the task list.

This source change does not run migrations or change database permissions in a deployed environment.
