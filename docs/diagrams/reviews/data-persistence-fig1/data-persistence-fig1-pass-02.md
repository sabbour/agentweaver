# Persistence · provider-selected, not one SQLite file — pass-02

Audience: technical readers of the deep-dive documentation.
Takeaway: Production PostgreSQL and local SQLite have different operational, event and checkpoint seams.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/data-persistence-fig1.drawio; publication: docs/diagrams/data-persistence-fig1.png.

## Actual image review

Opened data-persistence-fig1-pass-02.png enlarged and data-persistence-fig1-pass-02-print.png as an A5/96-dpi screen proof. Correction-only changes: vertical labels moved beside arrowheads; long labels shortened without changing their evidence-map relationships; outer labels that exceeded the page removed; MAF retry moved to a marigold outer rail; embedded materialization routed through a separate gutter; shared lower rows removed from provider/seam groups; native note fold corrected. A5 and enlarged review confirms the previously obscured arrowheads are visible. Remaining batch defects are the overview broker/delegate label collision and collinear opposite-direction center segments in typed adapters/service handoff; fan-out exit attachments also need distinct positions. Other layouts need no further content or composition changes.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **pg** — PostgreSQL / EF; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1026-1054.
- **sqlite** — Raw SQLite operations; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1026-1054.
- **pgevents** — Durable EF event log; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1026-1054.
- **localevents** — SQLite event stream; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1026-1054.
- **pgcheckpoint** — Shared checkpoints + leases; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1058-1075.
- **localcheckpoint** — File checkpoints / no-op lease; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1058-1075.
- **stream** — RunStreamStore snapshot; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:457-555.
- **workspace** — Workspace files; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317.

## Relationships

- `pg-to-pgevents`: pg → pgevents: selected event store. Evidence: apps/Agentweaver.Api/Program.cs:1026-1054.
- `sqlite-to-localevents`: sqlite → localevents: selected event store. Evidence: apps/Agentweaver.Api/Program.cs:1026-1054.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Columns are configuration alternatives, not failover. Durable event polling and local snapshots coexist.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.
