# sandbox-browser-preview-fig1 — pass 05

Corrected the source-copy encoding defect without changing the intended labels, content, geometry or arrows. Explicit UTF-8 restores the previously inspected pass-3 glyphs. Every connector was traced again on the newly exported PNG.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 0; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | after create | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:192-305; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380 | clean |
| e1 | n1 → n2 | ready URL | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380; docs/deep-dive/sandbox-browser-preview.md:64-68 | clean |
| e2 | n1 → n3 | HTTPS probe | Exit (0.65,1); entry (1,0.24); waypoints [('447.6', '288'), ('276', '288'), ('276', '343.2')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-380; k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284 | clean |
| e3 | n2 → n3 | HTTPS | Exit (0.35,1); entry (1,0.44); waypoints [('642.4', '270'), ('290', '270'), ('290', '369.2')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | docs/deep-dive/sandbox-browser-preview.md:64-68; k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284 | clean |
| e4 | n3 → n4 | route | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | k8s/base/gateway-preview.yaml; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:253-284; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:223-250; k8s/base/networkpolicy-sandbox.yaml | clean |
| e5 | n4 → n5 | public port | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:223-250; k8s/base/networkpolicy-sandbox.yaml; apps/Agentweaver.AgentHost/TcpPortForwarder.cs; docs/deep-dive/sandbox-browser-preview.md:71-98 | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
