# System diagnostics · a narrow live check map — pitch

Audience: technical readers of the deep-dive documentation.
Takeaway: The protected system snapshot combines checks and counts; it is not the detailed-health surface.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/api-core-fig6.drawio; publication: docs/diagrams/api-core-fig6.png.

## Actual image review

Opened api-core-fig6-pitch.png enlarged and api-core-fig6-pitch-print.png as an A5/96-dpi screen proof. The two concepts are legible, with warm cards, native process icons, accents and badges. The large intentional blank field and absence of detailed groups/relationships are pitch limitations to resolve in pass 1.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **request** — Diagnostics request; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:52.
- **service** — Diagnostics service; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:52.
- **storage** — SQLite + data directory; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- **builtins** — Built-in definitions; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- **heartbeat** — Heartbeat + project store; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- **github** — GitHub CLI / auth; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- **counts** — Counts + pod quota; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:105-112.
- **dto** — SystemDiagnosticsDto; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:116-134.

## Relationships

- `request-to-service`: request → service: collect. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:52.
- `service-to-storage`: service → storage: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- `service-to-builtins`: service → builtins: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:97-103.
- `service-to-heartbeat`: service → heartbeat: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- `service-to-github`: service → github: check. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:95-103.
- `service-to-counts`: service → counts: read counts. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:105-112.
- `github-to-dto`: github → dto: check results. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:116-134.
- `counts-to-dto`: counts → dto: summary. Evidence: apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:105-134.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Detailed health separately checks PostgreSQL, Key Vault, warm pool and Kubernetes; probes are cheaper.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.

## Handoff

Coarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.
