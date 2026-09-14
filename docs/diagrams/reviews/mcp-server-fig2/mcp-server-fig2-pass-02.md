# MCP: discover, consent, invoke — pass-02

Audience: Agentweaver implementers and operators.

Takeaway: API-owned OAuth issues broker tokens; MCP validates and forwards the same bearer.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/mcp-server-fig2.png. Target documentation: docs/deep-dive/mcp-server.md.

## Actual PNG inspection

Opened mcp-server-fig2-pass-02.png at enlarged export resolution and mcp-server-fig2-pass-02-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Corrections only: route declaration label no longer obscures its neighboring crossing; project-to-team now leaves a separate port from run-to-project. Those affected neighbors were rechecked. Other assets are independently exported no-change passes. All actual outputs remain legible at A5 and enlarged; orientation, label bounds, endpoints and arrowheads are clean.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| client: MCP client | native:uml (umlActor) | k8s/base/mcp-httproute.yaml:30-46 |
| token: API /oauth/token | native:flowchart (process) | apps/Agentweaver.Api/Program.cs:982-998 |
| authorize: API authorization server | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| refresh: Refresh grant checks | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| entra: Microsoft Entra ID | native:azure (image;image=img/lib/azure2/identity/Azure_Active_Directory.svg;imageAspect=1) | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| mcp: MCP broker validation | native:flowchart (process) | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:64-115 |
| consent: Consent decision | native:flowchart (hexagon) | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| api: API resource authorization | native:flowchart (process) | apps/Agentweaver.Mcp/AgentweaverApiClient.cs:353-391 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| client-to-authorize | client | authorize | discover issuer | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| authorize-to-entra | authorize | entra | if needed | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| entra-to-consent | entra | consent | identity | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| consent-to-token | consent | token | code + PKCE | apps/Agentweaver.Api/Program.cs:982-998 |
| token-to-refresh | token | refresh | renewal | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
| token-to-mcp | token | mcp | bearer | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:64-115 |
| mcp-to-api | mcp | api | same bearer | apps/Agentweaver.Mcp/AgentweaverApiClient.cs:353-391 |
