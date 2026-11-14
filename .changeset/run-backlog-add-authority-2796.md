---
"Agentweaver.Orchestrator": patch
---

Recheck run authority after the backlog graph lock and before task creation
commits. Roll back the task and graph changes when the writer loses access.
