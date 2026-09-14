# experience-onboarding-auth-fig2: pass-04

**Takeaway:** An Entra-backed consent flow issues the exact-resource credential accepted by MCP.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-00-overview-fig1/research-identity.md](../experience-00-overview-fig1/research-identity.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Correction-only review

No composition, content, or visual-language changes. Orientation, overlap, arrow endpoint/direction/routing checks found no permitted defect. Saved and re-exported a distinct source/PNG pair.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Zero remaining defects.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- MCP client: `native:flowchart`.
- Browser consent: `native:c4`.
- OpenIddict: `native:flowchart`.
- Authorized API: `native:flowchart`.
- MCP boundary: `native:flowchart`.
- Token exchange: `native:flowchart`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map and complete final arrow trace

- e0: MCP client -> Browser consent: open. `apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:87-138`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e1: Browser consent -> OpenIddict: allow. `apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:148-188`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e2: OpenIddict -> Token exchange: code grant. `apps/Agentweaver.Api/Endpoints/OAuthAuthorizationServerEndpoints.cs:31-76`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e3: Token exchange -> MCP boundary: tool bearer. `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-87`. Endpoint-attached, correctly directed, gutter-routed; clean.
- e4: MCP boundary -> Authorized API: forward. `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:359-382`. Endpoint-attached, correctly directed, gutter-routed; clean.
