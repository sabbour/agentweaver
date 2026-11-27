---
"Agentweaver.Projects.Config": minor
"Agentweaver.Gateway": minor
"Agentweaver.Policy.Acs": minor
---

Add human Project Policy baseline approval and revocation routes (`POST /api/v1/policies/projects/{projectId}/baseline-approvals` and `.../revoke`), the config-pinned operator `--project-policy-baseline` CLI (`pin` / `revoke`), and Platform-to-Project non-Skills structural and Rego module compatibility verification while keeping Project Policy head writes disabled.
