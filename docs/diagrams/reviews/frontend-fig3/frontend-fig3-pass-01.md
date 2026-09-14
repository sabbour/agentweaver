# Static hosting and API origins — pass-01

Audience: Agentweaver implementers and operators.

Takeaway: The Web host serves the SPA; API_URL is an origin or empty, never /api.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig3.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig3-pass-01.png at enlarged export resolution and frontend-fig3-pass-01-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. The expanded hierarchy is legible at A5 and enlarged. Native Azure identity and database symbols render correctly; accents remain 5 units. Directed arrowheads are visible after spacing/label corrections. The route declaration and ownership diagrams have the specific correction-only handoffs recorded below; remaining diagrams have no observed orientation, overlap or arrow defect.

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

## Growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2182,
  "result_meaningful_xml": 22842,
  "baseline_visible_structures": 8,
  "result_visible_structures": 84,
  "growth_ratio": 10.468378,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\frontend-fig3\\frontend-fig3-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\frontend-fig3\\frontend-fig3-pass-01.drawio"
}
```

Expansion is eight distinct source-backed nodes, tier surfaces, title/subtitle/detail/metadata/pill hierarchy, native symbols and relationship-specific orthogonal arrows. No invisible objects, off-page content, duplicate cells, comments, embedded images or metadata padding are used.
