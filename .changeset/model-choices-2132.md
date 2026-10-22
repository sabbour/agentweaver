---
"Agentweaver.Gateway": minor
"Agentweaver.Projects.Config": minor
"Agentweaver.Web": minor
---

Expose project-scoped model and platform provider choices, and limit project-level provider overrides to model configuration. Projects & Config now reads a validated, read-only copy of the model-binding eligibility fields (source mode, enabled state, BYOK reasoning efforts, and revision) and lists enabled bindings as model choices; hosted reasoning levels are reported as verified at run start. Without that copy, model choices report an explicit `model_capability_catalog_unavailable` state. Settings show owner-backed selects and keep ineligible saved references visible. Configuration writes reject any non-empty provider override while keeping stored legacy values.
