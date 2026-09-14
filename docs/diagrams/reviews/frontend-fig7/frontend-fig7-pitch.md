# Live timeline: independent inputs — pitch

Audience: Agentweaver implementers and operators.

Takeaway: REST seed and live SSE run concurrently, then merge into a guarded projection.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig7.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig7-pitch.png at enlarged export resolution and frontend-fig7-pitch-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Two scope anchors and native icons are legible at both views. The deliberately coarse association is not a full implementation flow; pass 1 must expand the subject-specific branches and authority boundaries.

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
| buffer-to-merge | buffer | merge | live events | apps/web/src/hooks/useSeededRunStream.ts:139-142 |
| merge-to-project | merge | project | merged | apps/web/src/timeline/mergeRunEvents.ts:23-79 |
| sse-to-reconnect | sse | reconnect | disconnect | apps/web/src/api/sse.ts:239-337 |
| buffer-to-stop | buffer | stop | done | apps/web/src/api/sse.ts:239-337 |

## Handoff

Coarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.
