# Static hosting and API origins — pitch

Audience: Agentweaver implementers and operators.

Takeaway: The Web host serves the SPA; API_URL is an origin or empty, never /api.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig3.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig3-pitch.png at enlarged export resolution and frontend-fig3-pitch-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Two scope anchors and native icons are legible at both views. The deliberately coarse association is not a full implementation flow; pass 1 must expand the subject-specific branches and authority boundaries.

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

## Handoff

Coarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.
