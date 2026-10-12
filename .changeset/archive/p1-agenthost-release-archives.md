---
"Agentweaver.AgentHost": patch
"Agentweaver.Gateway": patch
"Agentweaver.Mcp": patch
---

Prepare AgentHost through its existing pinned native-runtime Dockerfile.
Bind the Docker-save and OCI archive hashes to the same verified image config.
Remeasure OCI bytes and check the loaded native runtime before registry login.
Use the existing pinned SDK container base for Gateway and MCP.
This records release intent only. It does not apply versions, publish, or deploy.
