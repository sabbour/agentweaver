# auth-security-fig4 — pass 04

Separate no-layout-change export and complete arrow trace. PNG inspection caught Windows default-decoding corruption in Unicode annotation glyphs during source copying. This pass is retained as historical evidence, NOT the final promoted source. Diagrams containing only ASCII had no glyph defect.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 1; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | OAuth exchange | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | docs/deep-dive/auth-security.md:25-31; apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; docs/deep-dive/auth-security.md:25-31 | clean |
| e1 | n1 → n3 | broker token | Exit (0.35,1); entry (1,0.44); waypoints [('380.4', '282'), ('290', '282'), ('290', '369.2')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; docs/deep-dive/auth-security.md:25-31; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85 | clean |
| e2 | n2 → n3 | turn broker | Exit (0.65,1); entry (1,0.24); waypoints [('709.6', '288'), ('276', '288'), ('276', '343.2')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85 | clean |
| e3 | n3 → n4 | forward broker | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:45-49; 194-250 | clean |
| e4 | n4 → n5 | authorize | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:45-49; 194-250; docs/deep-dive/auth-security.md:29-31; apps/Agentweaver.Api/Security | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
