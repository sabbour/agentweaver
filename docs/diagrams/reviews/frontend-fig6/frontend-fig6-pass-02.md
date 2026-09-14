# Entra sign-in and browser session — pass-02

Audience: Agentweaver implementers and operators.

Takeaway: The callback returns a one-time code; session exchange delivers the SPA bearer.

Single editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/frontend-fig6.png. Target documentation: docs/deep-dive/frontend.md.

## Actual PNG inspection

Opened frontend-fig6-pass-02.png at enlarged export resolution and frontend-fig6-pass-02-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. Corrections only: route declaration label no longer obscures its neighboring crossing; project-to-team now leaves a separate port from run-to-project. Those affected neighbors were rechecked. Other assets are independently exported no-change passes. All actual outputs remain legible at A5 and enlarged; orientation, label bounds, endpoints and arrowheads are clean.

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
