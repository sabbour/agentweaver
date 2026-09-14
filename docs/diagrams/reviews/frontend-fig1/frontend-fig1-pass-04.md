# Frontend: intent and projection — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: The browser presents backend facts; the API remains the authority.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig1.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig1-pass-04.png at enlarged export resolution and frontend-fig1-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| auth: AuthGate | native:uml (umlActor) | apps/web/src/App.tsx:399-403 |
| controls: Rendered controls | native:flowchart (process) | apps/web/src/pages/CoordinatorRunPage.tsx:2842 |
| shell: AppShell | native:flowchart (process) | apps/web/src/components/shell/AppShell.tsx:134-182 |
| projection: Client projection | native:flowchart (process) | apps/web/src/pages/CoordinatorRunPage.tsx:2842 |
| pages: Route pages | native:flowchart (process) | apps/web/src/App.tsx:80-127 |
| live: Seed + live events | native:flowchart (process) | apps/web/src/hooks/useSeededRunStream.ts:43-151 |
| client: API client | native:flowchart (process) | apps/web/src/config.ts:13-36 |
| api: Agentweaver API | native:flowchart (process) | apps/web/src/pages/CoordinatorRunPage.tsx:2842 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| auth-to-shell | auth | shell | enter | apps/web/src/App.tsx:399-403 |
| shell-to-pages | shell | pages | contains | apps/web/src/App.tsx:80-127 |
| pages-to-client | pages | client | request | apps/web/src/pages/CoordinatorRunPage.tsx:2842 |
| client-to-api | client | api | HTTP | apps/web/src/config.ts:13-36 |
| api-to-live | api | live | history + SSE | apps/web/src/hooks/useSeededRunStream.ts:43-151 |
| live-to-projection | live | projection | events | apps/web/src/timeline/mergeRunEvents.ts:23-79 |
| projection-to-controls | projection | controls | render | apps/web/src/pages/CoordinatorRunPage.tsx:2842 |

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| auth-to-shell: auth → shell | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| shell-to-pages: shell → pages | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| pages-to-client: pages → client | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| client-to-api: client → api | (1, 0.5) → (0, 0.5) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| api-to-live: api → live | (0.5, 0) → (0.5, 1) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| live-to-projection: live → projection | (0.5, 0) → (0.5, 1) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| projection-to-controls: projection → controls | (0.5, 0) → (0.5, 1) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
