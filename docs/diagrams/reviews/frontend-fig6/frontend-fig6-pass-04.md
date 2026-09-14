# Entra sign-in and browser session — pass-04

Audience: Agentweaver implementers and operators.

Takeaway: The callback returns a one-time code; session exchange delivers the SPA bearer.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig6.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig6-pass-04.png at enlarged export resolution and frontend-fig6-pass-04-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Opened both newly exported enlarged and A5 proof images and traced every arrow against its cited relationship. Source departure and target arrowhead agree with the intended direction; branch lanes and ports remain separate, labels are readable, no node text is clipped, and native glyphs render correctly. No new feature or embellishment was added.

## Grounding

The user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.

## Native and custom symbols / visual credits

Fluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.

| Node | Symbol | Evidence |
|---|---|---|
| begin: Browser sign-in | native:uml (umlActor) | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| exchange: Session exchange | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| entra: Microsoft Entra ID | native:azure (image;image=img/lib/azure2/identity/Azure_Active_Directory.svg;imageAspect=1) | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| storage: Per-tab sessionStorage | native:database (cylinder3) | apps/web/src/config.ts:60-138 |
| callback: API callback | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| peer: Same-origin peer tab | native:flowchart (process) | apps/web/src/config.ts:207-240 |
| frontend: Frontend callback | native:flowchart (process) | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| requests: Authenticated requests | native:flowchart (process) | apps/web/src/api/sse.ts:239-337 |

## Directed relationship evidence

| ID | Source | Target | Relationship | Evidence |
|---|---|---|---|---|
| begin-to-entra | begin | entra | sign in | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| entra-to-callback | entra | callback | callback | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| callback-to-frontend | callback | frontend | code | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| frontend-to-exchange | frontend | exchange | POST code | apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs:398-455 |
| exchange-to-storage | exchange | storage | session | apps/web/src/config.ts:60-138 |
| storage-to-peer | storage | peer | transfer | apps/web/src/config.ts:207-240 |
| storage-to-requests | storage | requests | bearer | apps/web/src/api/sse.ts:239-337 |

## Final every-arrow trace

For every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.

| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |
|---|---|---|---|
| begin-to-entra: begin → entra | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| entra-to-callback: entra → callback | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| callback-to-frontend: callback → frontend | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| frontend-to-exchange: frontend → exchange | (1, 0.5) → (0, 0.5) | (413, 474) → (413, 144) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| exchange-to-storage: exchange → storage | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| storage-to-peer: storage → peer | (0.5, 1) → (0.5, 0) | Direct orthogonal card-to-card gutter | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
| storage-to-requests: storage → requests | (1, 0.5) → (1, 0.5) | (808, 254) → (808, 474) | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |
