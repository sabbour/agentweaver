---
"Agentweaver.Gateway": patch
"Agentweaver.Projects.Config": patch
---

Correct P1 setup route requirements to use the existing unbound `api.read` and
`projects.admin` scopes, matching the current TenantAdmin authority checks.
