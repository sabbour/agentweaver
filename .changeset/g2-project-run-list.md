---
"Agentweaver.Orchestrator": minor
"Agentweaver.Gateway": minor
"Agentweaver.Web": minor
---

Add a paged, newest-first project run list at
`GET /api/v1/projects/{projectId}/runs`, owned by the Orchestrator and routed
through the Gateway with matching OpenAPI schemas and web contract types.
Record run completion time in Orchestrator migration 022 so the list can show
completed runs.
