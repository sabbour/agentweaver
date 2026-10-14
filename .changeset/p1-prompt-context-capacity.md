---
"Agentweaver.AgentRuntime": patch
"Agentweaver.Identity": patch
"Agentweaver.AgentHost": patch
---

Pin AgentHost to native runtime 1.0.97 and apply configured prompt capacity as a native per-prompt context limit. The runtime narrows the accepted capacity to the concrete model and server limits, compacts removable history, and lets the native SDK fail explicitly when irreducible context cannot fit.
