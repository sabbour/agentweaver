---
"agentweaver": patch
---

Fix `project_create` MCP tool: the optional `blueprint` argument had no C# default value, so the
SDK's reflection-based argument binding treated it as required and rejected any call that omitted
it (the normal/documented case) with an opaque "An error occurred invoking 'project_create'." error
before the tool body ever ran. `blueprint` now defaults to `null` like the other optional
create-project fields.