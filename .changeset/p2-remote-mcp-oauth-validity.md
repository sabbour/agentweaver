---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity.Broker": minor
"Agentweaver.Gateway": minor
---

Identity reports current delegated Remote MCP credential validity for an expected configuration revision and checksum through `POST /internal/connections/remote-mcp/{connectionId}/validity`, and the Gateway exposes it at `POST /api/connections/remote-mcp/v1/{connectionId}/validity`. Abstractions adds `RemoteMcpCredentialValidity` and `RemoteMcpRemoteUse.Admit`, which combine Environment invocation validity with Identity credential validity before a remote call (#1927).
