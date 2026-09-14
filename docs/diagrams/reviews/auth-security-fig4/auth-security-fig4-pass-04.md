# auth-security-fig4 — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `auth-security-fig4-pass-04.png` (2× official draw.io export) and `auth-security-fig4-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: External MCP client | n1: Authorization server | OAuth exchange | docs/deep-dive/auth-security.md:25-31; apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; docs/deep-dive/auth-security.md:25-31 | external client right → authorization server left; OAuth exchange aggregate; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: Authorization server | n3: MCP validation | broker token | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs; docs/deep-dive/auth-security.md:25-31; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85 | issuer bottom → MCP right at the lower target port; label moved upstream from bridge; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n2: Assistant API | n3: MCP validation | turn broker | apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs; apps/Agentweaver.Api/Assistant/AssistantRunService.cs:789-813; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85 | assistant bottom → MCP right at the upper target port; separate gutter lane from issuer; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n3: MCP validation | n4: API broker handler | forward broker | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-85; apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:45-49; 194-250 | MCP right → API handler left below the incoming token ports; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n4: API broker handler | n5: Resource authorization | authorize | apps/Agentweaver.Api/Auth/AgentweaverAuthentication.cs:45-49; 194-250; docs/deep-dive/auth-security.md:29-31; apps/Agentweaver.Api/Security | API handler right → resource authorization left; target block arrowhead, no false junction dots | clean |
