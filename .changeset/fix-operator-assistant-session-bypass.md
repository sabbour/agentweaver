---
"agentweaver": patch
---

Fixed Operator Assistant turns failing 100% of the time on AgentHost pods.
`MapA2AHttpJson`'s session store calls `CreateSessionAsync` on every new A2A
message regardless of `AgentHostPurpose`, and `A2ATurnBridgeAgent` (a
`DelegatingAIAgent`) forwarded this unconditionally to the singleton
`CopilotAIAgent`. For the `OperatorAssistant` purpose, `AgentHostStartupService`
deliberately never calls `CopilotAIAgent.SetupAsync` (this purpose never drives
`CopilotAIAgent` — turns are routed to `IOperatorAssistantAgent` instead), so
`CopilotAIAgent.CreateSessionCoreAsync` threw
`InvalidOperationException("SetupAsync must be called before
CreateSessionAsync.")` before the turn ever executed. `A2ATurnBridgeAgent` now
overrides session creation to bypass `CopilotAIAgent` for the
`OperatorAssistant` purpose, matching how turn execution already routes around
it; all other purposes are unaffected.
