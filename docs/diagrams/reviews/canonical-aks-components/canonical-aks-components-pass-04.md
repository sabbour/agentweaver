# canonical-aks-components — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `canonical-aks-components-pass-04.png` (2× official draw.io export) and `canonical-aks-components-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Application ingress | n1: API deployment | HTTPS | k8s/base/frontend-deployment.yaml:10; k8s/base/gateway.yaml; k8s/base/gateway-preview.yaml; k8s/base/api-deployment.yaml:13,67-72,415-419 | application ingress right → API left; logical HTTPS request path; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n2: MCP deployment | n1: API deployment | API tools | k8s/base/mcp-deployment.yaml:10; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs; k8s/base/api-deployment.yaml:13,67-72,415-419 | MCP left → API right; explicitly left-facing forwarded-tool request; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n1: API deployment | n5: Durable services | persist / mount | k8s/base/api-deployment.yaml:13,67-72,415-419; k8s/base/api-deployment.yaml:67-72,415-419; k8s/base/pvc-workspace.yaml; k8s/base/worker-deployment.yaml:266-270 | API bottom → durable services top; bridge separates worker persistence crossing; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n3: Worker deployment | n5: Durable services | persist / mount | k8s/base/worker-deployment.yaml:13,67-72,266-270; k8s/base/worker-hpa.yaml:65-66; k8s/base/api-deployment.yaml:67-72,415-419; k8s/base/pvc-workspace.yaml; k8s/base/worker-deployment.yaml:266-270 | worker right → durable services top via inter-card then upper horizontal gutter; avoids AgentHost; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n3: Worker deployment | n4: AgentHost pods | claim + dispatch | k8s/base/worker-deployment.yaml:13,67-72,266-270; k8s/base/worker-hpa.yaml:65-66; k8s/base/sandbox-warmpool-agenthost.yaml; apps/Agentweaver.AgentHost/Program.cs:232-285 | worker right → AgentHost left below worker persistence exit; target block arrowhead, no false junction dots | clean |
