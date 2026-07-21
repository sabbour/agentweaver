---
"agentweaver": patch
---

Fix the live staging Operator Assistant outage where AgentHost pods could not reach the in-cluster
MCP service on port 8080 and every first turn timed out with `agenthost_unavailable`.

`agenthost-egress-allowlist` now includes an explicit, tightly-scoped egress allow from
AgentHost pods to `agentweaver-mcp` on TCP 8080, matching the live fix that restored
AgentHost -> MCP connectivity without broadening RFC1918 egress.
