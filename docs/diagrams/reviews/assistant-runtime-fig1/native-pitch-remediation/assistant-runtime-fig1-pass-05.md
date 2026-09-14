# assistant-runtime-fig1 — pass 05

Corrected the source-copy encoding defect without changing the intended labels, content, geometry or arrows. Explicit UTF-8 restores the previously inspected pass-3 glyphs. Every connector was traced again on the newly exported PNG.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 0; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | message | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Endpoints/AssistantEndpoints.cs; docs/deep-dive/assistant-runtime.md:36-40; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs | clean |
| e1 | n1 → n2 | configure / turn | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83; apps/Agentweaver.AgentHost/Program.cs:205-229 | clean |
| e2 | n2 → n5 | run turn | Exit (0.65,1); entry (0.65,0); waypoints [('709.6', '288'), ('709.6', '288')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83; apps/Agentweaver.AgentHost/Program.cs:205-229; packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs; docs/deep-dive/assistant-runtime.md:104-121 | clean |
| e3 | n5 → n4 | MCP tools | Exit (0,0.5); entry (1,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs; docs/deep-dive/assistant-runtime.md:104-121; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs | clean |
| e4 | n1 → n3 | append / reload | Exit (0.65,1); entry (1,0.3); waypoints [('447.6', '282'), ('282', '282'), ('282', '351.0')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; docs/deep-dive/assistant-runtime.md:39-42; apps/Agentweaver.Api/Assistant/AssistantRunService.cs | clean |
| e5 | n4 → n1 | API authorization | Exit (0.8,0); entry (0.8,1); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
