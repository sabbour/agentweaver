# sandbox-browser-preview-fig1 — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `sandbox-browser-preview-fig1-pass-04.png` (2× official draw.io export) and `sandbox-browser-preview-fig1-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Preview API | n1: Publication probe | after create | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:192-305; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380 | provisioner right → publication probe left; objects exist before exact-URL probe; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: Publication probe | n2: Browser preview | ready URL | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380; docs/deep-dive/sandbox-browser-preview.md:64-68 | probe right → browser left; ready URL returned only after proof; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n1: Publication probe | n3: Preview Gateway | HTTPS probe | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380; k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284 | probe bottom → Gateway right at upper target port; HTTPS, never direct pod TCP; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n2: Browser preview | n3: Preview Gateway | HTTPS | docs/deep-dive/sandbox-browser-preview.md:64-68; k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284 | browser bottom → Gateway right at lower target port; independent bridged gutter lane; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n3: Preview Gateway | n4: ClusterIP Service | route | k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:223-250; k8s/base/networkpolicy-sandbox.yaml | Gateway right → Service left below incoming HTTPS ports; target block arrowhead, no false junction dots | clean |
| e5 / 6 | n4: ClusterIP Service | n5: Sandbox preview app | public port | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:223-250; k8s/base/networkpolicy-sandbox.yaml; apps/Agentweaver.AgentHost/TcpPortForwarder.cs; docs/deep-dive/sandbox-browser-preview.md:71-98 | Service right → pod-local preview app left; target block arrowhead, no false junction dots | clean |
