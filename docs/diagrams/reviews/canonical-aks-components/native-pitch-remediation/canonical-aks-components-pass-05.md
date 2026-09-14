# canonical-aks-components — pass 05

Corrected the source-copy encoding defect without changing the intended labels, content, geometry or arrows. Explicit UTF-8 restores the previously inspected pass-3 glyphs. Every connector was traced again on the newly exported PNG.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 0; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | HTTPS | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | k8s/base/frontend-deployment.yaml:10; k8s/base/gateway.yaml; k8s/base/gateway-preview.yaml; k8s/base/api-deployment.yaml:13,67-72,415-419 | clean |
| e1 | n2 → n1 | API tools | Exit (0,0.5); entry (1,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | k8s/base/mcp-deployment.yaml:10; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs; k8s/base/api-deployment.yaml:13,67-72,415-419 | clean |
| e2 | n1 → n5 | persist / mount | Exit (0.65,1); entry (0.65,0); waypoints [('447.6', '288'), ('709.6', '288')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | k8s/base/api-deployment.yaml:13,67-72,415-419; k8s/base/api-deployment.yaml:67-72,415-419; k8s/base/pvc-workspace.yaml; k8s/base/worker-deployment.yaml:266-270 | clean |
| e3 | n3 → n5 | persist / mount | Exit (1,0.3); entry (0.85,0); waypoints [('282', '351.0'), ('282', '276'), ('754.4', '276')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | k8s/base/worker-deployment.yaml:13,67-72,266-270; k8s/base/worker-hpa.yaml:65-66; k8s/base/api-deployment.yaml:67-72,415-419; k8s/base/pvc-workspace.yaml; k8s/base/worker-deployment.yaml:266-270 | clean |
| e4 | n3 → n4 | claim + dispatch | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | k8s/base/worker-deployment.yaml:13,67-72,266-270; k8s/base/worker-hpa.yaml:65-66; k8s/base/sandbox-warmpool-agenthost.yaml; apps/Agentweaver.AgentHost/Program.cs:232-285 | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
