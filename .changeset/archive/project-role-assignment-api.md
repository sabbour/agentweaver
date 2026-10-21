---
"Agentweaver.Gateway": patch
"Agentweaver.Projects.Config": patch
---

Add project role-assignment routes. A current tenant admin can assign and revoke project roles, and a tenant admin or project Owner can list active assignments. Each change is audited in the same transaction. The runtime database grants for assign and revoke are defined but not applied. The P1 harness now sets up and cleans up its fixture project through these public routes.