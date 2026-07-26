---
"agentweaver": patch
---

Log durable, redacted telemetry for `start_preview` tool-call failures in
AgentHost. AgentHost sandbox pods are ephemeral and recycled shortly after a
run completes, so a non-success HTTP response (e.g. a 403) or an unhandled
exception from the `start_preview` tool's callback previously left no
durable evidence to investigate after the fact. `PreviewPublishTool` now
logs a structured event (tool name, run id, port, HTTP status code,
redacted+truncated response body or exception message) via the existing
`SandboxToolContext.Logger`, which already flows through to Application
Insights wherever `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured.
Anything token/secret-shaped is redacted via
`Agentweaver.SandboxExec.SandboxOutputRedactor` before being logged.
