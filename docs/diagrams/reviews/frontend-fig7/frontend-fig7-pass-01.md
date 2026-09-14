# Live timeline: independent inputs — pass-01

Audience: Agentweaver implementers and operators.

Takeaway: REST seed and live SSE run concurrently, then merge into a guarded projection.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig7.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig7-pass-01.png at enlarged export resolution and frontend-fig7-pass-01-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. The expanded hierarchy is legible at A5 and enlarged. Native Azure identity and database symbols render correctly; accents remain 5 units. Directed arrowheads are visible after spacing/label corrections. The route declaration and ownership diagrams have the specific correction-only handoffs recorded below; remaining diagrams have no observed orientation, overlap or arrow defect.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| run: Current run ID | native:flowchart (process) | apps/web/src/hooks/useSeededRunStream.ts:43-151 |
| merge: Merge run events | native:flowchart (process) | apps/web/src/timeline/mergeRunEvents.ts:23-79 |
| rest: Persisted event history | native:database (cylinder3) | apps/web/src/hooks/useSeededRunStream.ts:85-134 |
| project: Timeline projection | native:flowchart (process) | apps/web/src/pages/CoordinatorRunPage.tsx:2842 |
| sse: Live event transport | native:flowchart (process) | apps/web/src/api/sse.ts:239-337 |
| buffer: Parser and event buffer | native:flowchart (process) | apps/web/src/api/sse.ts:239-337 |
| reconnect: Unexpected disconnect | native:flowchart (process) | apps/web/src/api/sse.ts:239-337 |
| stop: done / terminal | native:flowchart (doubleEllipse) | apps/web/src/api/sse.ts:239-337; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:514-555 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| run-to-rest | run | rest | seed | apps/web/src/hooks/useSeededRunStream.ts:85-134 |
| run-to-sse | run | sse | live | apps/web/src/hooks/useSeededRunStream.ts:43-51 |
| rest-to-merge | rest | merge | seed events | apps/web/src/hooks/useSeededRunStream.ts:139-142 |
| sse-to-buffer | sse | buffer | frames | apps/web/src/api/sse.ts:239-337 |
| buffer-to-merge | buffer | merge | events | apps/web/src/hooks/useSeededRunStream.ts:139-142 |
| merge-to-project | merge | project | merged | apps/web/src/timeline/mergeRunEvents.ts:23-79 |
| sse-to-reconnect | sse | reconnect | disconnect | apps/web/src/api/sse.ts:239-337 |
| buffer-to-stop | buffer | stop | done | apps/web/src/api/sse.ts:239-337 |

## Growth gate

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 2165,
  "result_meaningful_xml": 24182,
  "baseline_visible_structures": 8,
  "result_visible_structures": 87,
  "growth_ratio": 11.169515,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true,
  "pitch": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\frontend-fig7\\frontend-fig7-pitch.drawio",
  "pass_one": "C:\\Users\\asabbour\\Git\\agentweaver\\.worktrees\\drawio-diagram-authoring\\docs\\diagrams\\reviews\\frontend-fig7\\frontend-fig7-pass-01.drawio"
}
```

Expansion is eight distinct source-backed nodes, tier surfaces, title/subtitle/detail/metadata/pill hierarchy, native symbols and relationship-specific orthogonal arrows. No invisible objects, off-page content, duplicate cells, comments, embedded images or metadata padding are used.
