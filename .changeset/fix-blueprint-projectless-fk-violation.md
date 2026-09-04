---
"agentweaver": patch
---

Fix blueprint generation for platform-scoped (project-less) runs by allowing `marketplace_copilot_capabilities.project_id` to be null instead of substituting a fake singleton project id, which violated the foreign key constraint and surfaced as an opaque 502.
