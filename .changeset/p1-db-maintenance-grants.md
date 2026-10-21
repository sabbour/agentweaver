---
"Agentweaver.Persistence.Postgres": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.EventsAndSessions": minor
---

Orchestrator and Events and Sessions migration runs can now apply and verify the reviewed P1 runtime grants.
The reviewed plan is embedded in the image. A run checks its exact hash, the planned runtime role and schema, and the compiled migration catalog before it connects.
After migrating, it grants only what the plan names, reads every grant back, and prints a marker that the database maintenance runner requires.
Without the new migration settings, `--migrate` behaves as before.
