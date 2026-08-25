---
"agentweaver": patch
---

Fix a real, still-present backend gap (issue #850 follow-up — the earlier claim that PR #853
fully resolved this was incorrect): tools built via `IAgentRuntimeToolProvider` (`start_preview`,
`start_preview_process`, `observe_bound_port`, `health_check`, `stop_preview_process`) never
emitted `tool.call`/`tool.result`/`tool.error` RunEvents or opened an `execute_tool` OTel span,
because they're invoked through the SDK's external-tool lifecycle
(`ExternalToolRequestedEvent`/`ExternalToolCompletedEvent`), whose completion event carries no
result content and which never opens a span. Every such tool is now wrapped in a new
`InstrumentedCustomAIFunction` that mints its own call id and emits the call/result/error events
and span directly around the real invocation, so the trace panel's "Arguments"/"Output" for
`start_preview` (and its siblings) are populated instead of showing "not recorded for this call".
