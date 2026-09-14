# experience-00-overview-fig1: pass-01

**Takeaway:** Web and MCP share authorization and authoritative state; events flow back to clients.

**Author:** GPT-6 Astra. **Page:** one uncompressed A5 landscape, 827 x 583 draw.io units.

**Export:** official draw.io Desktop 31.4.5; PNG, scale 2, border 16. Actual exported PNG and its 827 px print-size derivative opened and inspected.

**Grounding:** [experience-00-overview-fig1/research-identity.md](../experience-00-overview-fig1/research-identity.md). Three independently launched Astra threads: identity, execution, orchestration. Implementation/config/tests outrank legacy diagrams and captures.

## Visual upgrade

Metric: visible-semantic-canonical-xml-v1. Baseline 1029; result 18967; ratio 18.432459x.

Growth decomposes the two conceptual endpoints into six distinct evidenced responsibilities or outcomes, with tiered boundaries, native icons, titles, subtitles, metadata, badges, visible qualifications and endpoint-attached connectors. No padding, invisible objects, off-page cells, duplicate cells, comments or image bytes contribute.

Reading direction, card/boundary separation, title/detail fit, label association and arrow direction reviewed at print size and enlarged. Correction handoff: The API-result label crowds its arrowhead.

## Symbols and credits

Based on the repository Fluent template and loaded fluent-library.xml; theme corroborated by apps/web/src/theme.ts and CoordinatorTopologyGraph.tsx. Warm canvas/cards, Segoe UI, Cascadia Code metadata, 16 px radii, 5 px accents, shadows, semantic badges, orthogonal connectors and native bridge arcs preserved.

- Human operator: `native:c4`.
- Web UI: `native:flowchart`.
- MCP client: `native:flowchart`.
- Product state: `custom:agentweaver`.
- Agentweaver API: `native:flowchart`.
- MCP server: `native:flowchart`.

Native shapes come from the bundled draw.io Desktop 31.4.5 libraries (https://github.com/jgraph/drawio-desktop/releases/tag/v31.4.5); draw.io is Apache-2.0. Vendor symbols identify their actual technology; no separate logos, screenshots or third-party image bytes were acquired. Custom hexagon marks only product-specific concepts.

## Evidence map

- e0: Human operator -> Web UI: inspect. `apps/web/src/App.tsx:103-125`. Trace pending correction passes.
- e1: Web UI -> Agentweaver API: requests. `apps/web/src/api/sse.ts:237-246`. Trace pending correction passes.
- e2: Agentweaver API -> Web UI: SSE events. `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:481-514`. Trace pending correction passes.
- e3: MCP client -> MCP server: tool call. `apps/Agentweaver.Mcp/Program.cs:85-98`. Trace pending correction passes.
- e4: MCP server -> MCP client: result. `apps/Agentweaver.Mcp/Tools/RunTools.cs:229-265`. Trace pending correction passes.
- e5: MCP server -> Agentweaver API: forward. `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:359-382`. Trace pending correction passes.
- e6: Agentweaver API -> MCP server: API result. `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:376-382`. Trace pending correction passes.
- e7: Agentweaver API -> Product state: access. `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:158-162`. Trace pending correction passes.
