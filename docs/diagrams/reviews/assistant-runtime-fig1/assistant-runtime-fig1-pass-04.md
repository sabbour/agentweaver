# assistant-runtime-fig1 — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `assistant-runtime-fig1-pass-04.png` (2× official draw.io export) and `assistant-runtime-fig1-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Sessions UI | n1: Assistant API | message | apps/Agentweaver.Api/Endpoints/AssistantEndpoints.cs; docs/deep-dive/assistant-runtime.md:36-40; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs | UI right → API left, straight request arrow; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: Assistant API | n2: Held AgentHost | configure / turn | apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83; apps/Agentweaver.AgentHost/Program.cs:205-229 | API right → held pod left, straight configure/turn arrow; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n2: Held AgentHost | n5: Fresh SDK session | run turn | apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83; apps/Agentweaver.AgentHost/Program.cs:205-229; packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs; docs/deep-dive/assistant-runtime.md:104-121 | held pod bottom → SDK top, vertical execution arrow; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n5: Fresh SDK session | n4: MCP server | MCP tools | packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs; docs/deep-dive/assistant-runtime.md:104-121; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs | SDK left → MCP right, left-facing tool arrow; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n1: Assistant API | n3: Run + event store | append / reload | apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; docs/deep-dive/assistant-runtime.md:39-42; apps/Agentweaver.Api/Assistant/AssistantRunService.cs | API bottom → store right through the lower-left gutter; append/reload operation, not a return-data arrow; target block arrowhead, no false junction dots | clean |
| e5 / 6 | n4: MCP server | n1: Assistant API | API authorization | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs | MCP top → API bottom at a separate port from the persistence path; target block arrowhead, no false junction dots | clean |
