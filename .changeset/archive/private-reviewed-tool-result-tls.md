---
"Agentweaver.Identity": patch
"Agentweaver.AgentRuntime": patch
"Agentweaver.Gateway": patch
"Agentweaver.Orchestrator": patch
"Agentweaver.EventsAndSessions": patch
---

Add configured direct mutual-TLS routes for private reviewed tool results.
Check native TLS before reading bodies, retain current actor and owner checks,
and store result bytes before journal references and acknowledgments.
Recover matching saved results before remote dispatch and recheck authority before returning bytes.
Keep private reviewed tool result material out of public Gateway projections.

Missing TLS configuration remains disabled.
The ordinary AgentHost material client and native remote-tool hook remain disabled.
This source does not establish live TLS, remote-tool execution, or deployment acceptance.
