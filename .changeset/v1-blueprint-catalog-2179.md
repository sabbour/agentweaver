---
"Agentweaver.Projects.Config": minor
"Agentweaver.Gateway": minor
"Agentweaver.Web": minor
---

Add the 0.x blueprint catalog to Projects & Config: list, validate, and apply blueprints as reviewed casting proposals. The proposal lists skill bindings it can't apply and never changes a skill's enabled setting. Add a build-time converter that turns 0.x blueprints and blueprint packages into native AgentSuite packages, checking packages with the ported 0.x validator, plus the blueprint projection checks and apply path. An AgentSuite import with `blueprint` now validates and applies it to the proposal and returns `unresolvedSkillBindings`.

Describe unresolved blueprint skill bindings in the Gateway OpenAPI response and the web import result contract.
