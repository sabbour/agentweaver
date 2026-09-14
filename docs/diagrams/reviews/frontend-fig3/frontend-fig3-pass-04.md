# Static hosting and API origins — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: The Web host serves the SPA; API_URL is an origin or empty, never /api.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig3.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig3-pass-04.png at enlarged export resolution and frontend-fig3-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| browser: Browser request | native:uml (umlActor) | apps/Agentweaver.Web/Program.cs:39-65 |
| config: Runtime configuration | native:flowchart (process) | apps/web/src/config.ts:13-36 |
| static: Static file middleware | native:uml (folder) | apps/Agentweaver.Web/Program.cs:39-50 |
| client-origin: Origin resolution | native:flowchart (process) | apps/web/src/config.ts:13-36 |
| fallback: SPA route fallback | native:flowchart (process) | apps/Agentweaver.Web/Program.cs:61-65 |
| api-host: API destination | native:flowchart (process) | apps/web/src/api/sse.ts:239-337 |
| docs-route: Documentation route | native:flowchart (process) | apps/Agentweaver.Web/Program.cs:52-59 |
| external-docs: External documentation | native:cloud (cloud) | apps/Agentweaver.Web/Program.cs:52-59 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| browser-to-static | browser | static | asset | apps/Agentweaver.Web/Program.cs:39-50 |
| static-to-fallback | static | fallback | unmatched | apps/Agentweaver.Web/Program.cs:61-65 |
| config-to-client-origin | config | client-origin | supplies | apps/web/src/config.ts:13-36 |
| client-origin-to-api-host | client-origin | api-host | requests | apps/web/src/api/sse.ts:239-337 |
| docs-route-to-external-docs | docs-route | external-docs | 302 redirect | apps/Agentweaver.Web/Program.cs:52-59 |

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| browser-to-static: browser → static | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| static-to-fallback: static → fallback | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| config-to-client-origin: config → client-origin | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| client-origin-to-api-host: client-origin → api-host | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| docs-route-to-external-docs: docs-route → external-docs | (1, 0.5) → (0, 0.5) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
