# MCP: discover, consent, invoke — pitch

Audience: Agentweaver implementers and operators.

Takeaway: API-owned OAuth issues broker tokens; MCP validates and forwards the same bearer.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/mcp-server-fig2.png. Target documentation: docs/deep-dive/mcp-server.md.

## Actual PNG inspection

Opened mcp-server-fig2-pitch.png at enlarged export resolution and mcp-server-fig2-pitch-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Two scope anchors and native icons are legible at both views. The deliberately coarse association is not a full implementation flow; pass 1 must expand the subject-specific branches and authority boundaries.

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
| entra: Microsoft Entra ID | native:azure (mxgraph.azure2.azure_active_directory) | apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190 |
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
| token-to-mcp | token | mcp | broker bearer | apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:64-115 |
| mcp-to-api | mcp | api | same bearer | apps/Agentweaver.Mcp/AgentweaverApiClient.cs:353-391 |

## Handoff

Coarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.
