# Live timeline: independent inputs — pass-02

Audience: Agentweaver implementers and operators.

Takeaway: REST seed and live SSE run concurrently, then merge into a guarded projection.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig7.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig7-pass-02.png at enlarged export resolution and frontend-fig7-pass-02-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Corrections only: route declaration label no longer obscures its neighboring crossing; project-to-team now leaves a separate port from run-to-project. Those affected neighbors were rechecked. Other assets are independently exported no-change passes. All actual outputs remain legible at A5 and enlarged; orientation, label bounds, endpoints and arrowheads are clean.

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
