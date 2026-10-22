---
"Agentweaver.Gateway": minor
"Agentweaver.Projects.Config": minor
"Agentweaver.Web": minor
---

Expose project-scoped model and platform provider choices, and limit project-level provider overrides to model configuration. Model choices report an explicit `model_capability_catalog_unavailable` state with saved references listed as unverified, and configuration writes reject any non-empty provider override while keeping stored legacy values.
