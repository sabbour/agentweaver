---
"Agentweaver.Persistence.Postgres": minor
"Agentweaver.Orchestrator": minor
"Agentweaver.EventsAndSessions": minor
"Agentweaver.FoundationProbe": patch
---

Orchestrator and Events and Sessions migration runs can now apply and verify the reviewed P1 runtime grants.
The reviewed plan is embedded in the image. A run checks its exact hash, the planned runtime role and schema, and the compiled migration catalog before it connects.
After migrating, it grants only what the plan names, reads every grant back, and prints a marker that the database maintenance runner requires.
Without the new migration settings, `--migrate` behaves as before.
The Foundation Probe image build now copies the embedded plan file that its Postgres package needs.
A new setup command creates the database maintenance workflow identity, its namespace-scoped Kubernetes role, and the GitHub environment, and runs the one-time schema ownership handover.
The database maintenance runner compares the schema range each running and target service image accepts, then deploys before or after the migrations, or runs a planned readiness gap that deploys the matching images in the same run and only rolls forward.
