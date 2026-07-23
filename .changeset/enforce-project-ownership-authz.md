---
"agentweaver": patch
---

Enforce project ownership on all project-scoped memory, decision, session, and casting
endpoints. Previously these routes verified only that a project existed, so any
authenticated organization member who learned another project's UUID could read or modify
its memory, sessions, and decisions or hijack its agent-team casting. Because active
decisions are compiled verbatim into future agent system prompts, this also closed a
stored cross-project prompt-injection (XPIA) vector. A centralized `ProjectAuthorization`
guard now authorizes the caller against the project owner (the trusted internal
service identity used for a run's own agent callbacks remains exempt), covering both the
direct API and the MCP tools that proxy to these same routes.
