# Astra research: network, storage and Worker

Completed read-only thread: `guide-network-evidence`, model `gpt-6-astra`.
This coordinator summary persists the bounded agent result. Scope: effective
routing/policy unions, storage and Worker configuration.

## Findings adopted

- `k8s/base/gateway.yaml:9-39`, `httproute-api.yaml:19-63`,
  `mcp-httproute.yaml:14-46`, `httproute-frontend.yaml:22-34`, and Service manifests:
  application Gateway -> HTTPRoute -> Service -> pod. API/MCP Service 8080,
  Frontend Service 80, all targeting pod 8080.
- API prefixes `/api`, `/auth`, `/openapi`; exact discovery and OAuth endpoints.
  There is no blanket `/oauth` prefix. MCP uses `/mcp`, exact protected-resource
  metadata, and `/mcp/health` rewritten to `/healthz`.
- `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:213-276,1116-1167`
  and `k8s/base/gateway-preview.yaml:12-54`: separate preview hostname, Gateway,
  dynamic HTTPRoute and Service 80 -> preview target port; localhost hostname
  rewrite. The API manages resources, not browser traffic.
- `networkpolicy-sandbox.yaml:43-137`, `networkpolicy-agenthost-egress.yaml:46-107`,
  `networkpolicy-agenthost.yaml:28-73`, `networkpolicy-mcp.yaml:60-79`: policies
  union their allows. API/Worker reach A2A 8088; preview Gateway 3000-9000 also
  includes 8088. mTLS remains an independent boundary.
- AgentHost -> API/MCP selector-based TCP 8080; DNS UDP/TCP 53 to kube-dns or
  10.0.0.10/32. HTTPS 443 excludes IPv4 10/8, 172.16/12, 192.168/16,
  169.254/16, IPv6 fc00::/7 and fe80::/10. It does not exclude 100.64/10.
  Kata egress is not effectively FQDN-only. Reachability grants no vault authority.
- Worker deployment replicas 2; HPA 2-3, CPU 70% / memory 80%. API and Worker use
  durable EF stores, not a production Dapper-read / EF-write partition.
- Postgres -> MemoryDb -> Database connection precedence; shared 50 Gi RWX
  azurefile-csi-premium-uid1000 PVC and separate 8 Gi emptyDir scratch.

## Audit refinements

Explicitly disclose that the preview ingress range includes A2A 8088 rather than
claiming an exclusive API/Worker network boundary. Keep precise route/policy
tables in prose, with the diagram summarizing both ingress planes.
