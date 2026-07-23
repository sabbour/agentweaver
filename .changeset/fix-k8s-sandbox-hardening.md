---
"agentweaver": patch
---

Harden K8s/Kata sandbox isolation (security):

- **Sandbox egress**: `sandbox-egress-allowlist` no longer permits `0.0.0.0/0` on
  all ports. It is now scoped to public TCP/443 only and denies RFC1918, CGNAT/link-local,
  and IPv6 ULA/link-local ranges — blocking lateral movement to in-cluster
  Services/nodes/VNet and IMDS SSRF, matching the proven `agenthost-egress-allowlist`.
- **Public MCP identity**: the internet-exposed MCP now runs as a dedicated,
  least-privilege `agentweaver-mcp` ServiceAccount (no binding to the pod-create/exec
  sandbox Role, default token automount disabled) instead of sharing `agentweaver-api`,
  removing a namespace privilege-escalation path.
- **AgentHost A2A mTLS**: the production overlay enables mutual TLS + hostname
  verification for the `/configure` credential channel (encrypts the GitHub/turn tokens
  that previously crossed the pod network over plain HTTP).

Shared RWX workspace per-run isolation (Alert 2) is documented as follow-up
architectural work; the compounding egress and identity controls are hardened here.
