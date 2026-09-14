# auth-security-fig1 — pass 04

Separate no-layout-change export and complete arrow trace. PNG inspection caught Windows default-decoding corruption in Unicode annotation glyphs during source copying. This pass is retained as historical evidence, NOT the final promoted source. Diagrams containing only ASCII had no glyph defect.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 4; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | classify | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:20-57; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57 | clean |
| e1 | n1 → n2 | otherwise | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:131-153 | clean |
| e2 | n1 → n3 | eligible | Exit (0.65,1); entry (1,0.3); waypoints [('447.6', '288'), ('282', '288'), ('282', '351.0')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:28-57; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:32-55; 159-250 | clean |
| e3 | n2 → n4 | authenticated | Exit (0.35,1); entry (0.35,0); waypoints [('642.4', '270'), ('380.4', '270')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:131-153; apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23 | clean |
| e4 | n3 → n4 | authenticated | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:32-55; 159-250; apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23 | clean |
| e5 | n4 → n5 | authorized | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Security/ProjectAuthorization.cs:56-85,114-135; docs/deep-dive/auth-security.md:5-23; docs/deep-dive/auth-security.md:5-23; apps/Agentweaver.Api/Security | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
