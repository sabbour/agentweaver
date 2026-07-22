---
"agentweaver": patch
---

Fix a live-deploy-blocking MCP server startup crash: `project_create`'s optional `blueprint`
parameter changed from `JsonElement?` to `string?` (a JSON-encoded string), fixing a regression of
the 7605b692/#419 landmine. `Microsoft.Extensions.AI`'s reflection-based schema exporter cannot
serialize the default/uninitialized state of a `Nullable<JsonElement>` parameter into the tool's
JSON schema, which crashed the whole MCP server at boot (`AIJsonUtilities.CreateFunctionJsonSchema`
-> `InvalidOperationException` during `MapMcp`). Using `string?` keeps the parameter optional (so
`WithToolsFromAssembly` still binds calls that omit it, without a required-parameter binding
rejection) while remaining safely serializable as a schema default. Added a regression test that
launches the real compiled Agentweaver.Mcp process and asserts clean startup, since this bug only
reproduces with the exact dependency versions Agentweaver.Mcp resolves at runtime.
