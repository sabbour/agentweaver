# canonical-aks-network: pass 04

Mode: correction-only. Reviewer/model: GPT-6 Astra (`gpt-6-astra`).
Actual inspected evidence: `canonical-aks-network-pass-04.png` enlarged and
`canonical-aks-network-pass-04-print.png` at approximate A5 print scale.
The images were opened, not inferred solely from XML.

No new content. Re-exported and traced all ten arrows: two independent four-edge ingress chains and two AgentHost egress edges. No browser/API-proxy or vault-access edge is implied. DNS and A2A exceptions remain explicit annotations.

Observed remaining issue clusters: orientation/in-page 0,
overlap/fitting 0, arrows 0.
These are human inspection findings, not an automated overlap-score claim.
Passes 2-4 preserve semantic node/edge identity; only visibility, fit, labels and
endpoint/routing defects are corrected. No expansion to meet a later growth target.

Draw.io SHA256: `078fea154db8197c7ef0dd4becac8e68777be1fdc2c531d961dcfc38324dcb12`.
PNG SHA256: `95b075e41dc72b92412689c42d9bcabb64936df6b2dc432e9083f2498c91f4a4`.

## Complete arrow trace

- **n-client** `client -> gateway`: Rightward application client to TLS Gateway; no preview sharing implied. Evidence: k8s/base/gateway.yaml:9-39; httproute-api.yaml:19-63; mcp-httproute.yaml:14-46; httproute-frontend.yaml:22-34; api-service.yaml:9-17; mcp-service.yaml:9-17; frontend-service.yaml:9-17
- **n-gateway** `gateway -> routes`: Rightward app Gateway to HTTPRoutes; resource chain, not a process invocation. Evidence: k8s/base/gateway.yaml:9-39; httproute-api.yaml:19-63; mcp-httproute.yaml:14-46; httproute-frontend.yaml:22-34; api-service.yaml:9-17; mcp-service.yaml:9-17; frontend-service.yaml:9-17
- **n-routes** `routes -> services`: Rightward route selection to Services; exact API/OAuth/MCP details stay in prose. Evidence: k8s/base/gateway.yaml:9-39; httproute-api.yaml:19-63; mcp-httproute.yaml:14-46; httproute-frontend.yaml:22-34; api-service.yaml:9-17; mcp-service.yaml:9-17; frontend-service.yaml:9-17
- **n-services** `services -> pods`: Rightward Services to app pods; frontend 80 and API/MCP 8080 all target 8080. Evidence: k8s/base/gateway.yaml:9-39; httproute-api.yaml:19-63; mcp-httproute.yaml:14-46; httproute-frontend.yaml:22-34; api-service.yaml:9-17; mcp-service.yaml:9-17; frontend-service.yaml:9-17
- **n-browser** `browser -> pgateway`: Rightward preview browser to separate preview Gateway. Evidence: apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:213-276,1116-1167; k8s/base/gateway-preview.yaml:12-54
- **n-pgateway** `pgateway -> proute`: Rightward preview Gateway to dynamic HTTPRoute. Evidence: apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:213-276,1116-1167; k8s/base/gateway-preview.yaml:12-54
- **n-proute** `proute -> pservice`: Rightward preview route to dynamic Service, with localhost rewrite annotation. Evidence: apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:213-276,1116-1167; k8s/base/gateway-preview.yaml:12-54
- **n-pservice** `pservice -> agenthost`: Rightward preview Service to AgentHost target; no API proxy arrow. Evidence: apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:213-276,1116-1167; k8s/base/gateway-preview.yaml:12-54
- **n-internal** `agenthost -> internal`: AgentHost bottom along y=362 to API/MCP top; downward head, TCP 8080 label on rail. Evidence: k8s/base/networkpolicy-sandbox.yaml:43-137; networkpolicy-agenthost-egress.yaml:46-107; networkpolicy-agenthost.yaml:28-73; networkpolicy-mcp.yaml:60-79
- **n-public** `agenthost -> public`: AgentHost right around y=374 to public HTTPS top; downward head, TCP 443 label, title clear. Evidence: k8s/base/networkpolicy-sandbox.yaml:43-137; networkpolicy-agenthost-egress.yaml:46-107; networkpolicy-agenthost.yaml:28-73; networkpolicy-mcp.yaml:60-79

Every actual XML edge is covered exactly once. PNG inspection checked source, destination, direction, head visibility, label association, crossings and false junctions. Final identified defects: zero.
