---
'agentweaver': patch
---

Fix `start_preview` (and other `IAgentRuntimeToolProvider`-built tools) failing with
an opaque "Tool execution failed" on warm-pool AgentHost pods. The per-turn API
base URL/key resolved by `CopilotAIAgent.BuildSessionConfigTools` was never
forwarded to tool providers, so `PreviewRunnerToolProvider` always fell back to the
unreachable `http://localhost:5000` default (#335 P1 follow-up).
