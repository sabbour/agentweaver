---
"agentweaver": patch
---

Fix native agent tool calls (submit_decision, memory, inbox, …) silently timing out (~100s)
from inside sandbox agent-host pods on the AKS/Cilium staging cluster.

Agent-host pods could not reach the in-cluster `agentweaver-api` Service on TCP 8080. Under
Cilium, an in-cluster ClusterIP resolves to the destination pod's security identity, and only
an identity-based (`podSelector`) egress rule authorizes it — a CIDR `ipBlock` allow (even the
`0.0.0.0/0` rule in `sandbox-egress-allowlist`) matches only the "world"/CIDR entity, never a
cluster-managed pod identity. The MCP dependency already had such a rule; the API did not.

`agenthost-egress-allowlist` now adds an explicit, tightly-scoped `podSelector` egress allow
from agent-host pods to `agentweaver-api` on TCP 8080 (mirroring the existing MCP rule), so
API-backed native tools connect east-west instead of black-holing against the RFC1918 egress
exclusions of the SandboxTemplate-owned network policy. RFC1918 egress is not otherwise widened.
