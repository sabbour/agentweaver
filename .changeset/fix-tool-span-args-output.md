---
"agentweaver": patch
---

Fix issue #850 follow-up: custom tools registered by an `IAgentRuntimeToolProvider` (`start_preview`, `start_preview_process`, `observe_bound_port`, `health_check`, `stop_preview_process`) never recorded `tool.call`/`tool.result`/`tool.error` RunEvents or an `execute_tool` OTel span, since they go through the SDK's `ExternalToolRequestedEvent`/`ExternalToolCompletedEvent` pairing rather than the native `ToolExecutionStartEvent`/`ToolExecutionCompleteEvent` lifecycle PR #853 instrumented. The Execute Tool detail panel showed "No arguments/output recorded for this call" for these tools even though they executed successfully. Provider tools are now wrapped so their arguments and output are recorded (redacted) directly around the real invocation, sharing one callId across the span and RunEvents.
