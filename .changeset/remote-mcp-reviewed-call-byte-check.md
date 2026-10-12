---
"Agentweaver.Orchestrator": patch
---

Check the caller's private MCP request bytes before its late authority check and transport handoff.
Reject changed tools or arguments without a send or result write.
The native receiver must still check its actual upstream request; this change does not enable transport, credentials, or network enforcement.
