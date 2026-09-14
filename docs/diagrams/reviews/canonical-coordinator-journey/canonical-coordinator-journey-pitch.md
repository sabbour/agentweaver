# canonical-coordinator-journey: pitch

Audience: Agentweaver users and architecture readers.
Takeaway: Confirm intent, dispatch bounded work, then integrate and review the whole result.
Orientation: A5-landscape, one uncompressed editable 827 x 583 page.
Disposition: redesign (retain preserves supported identity, not the old rendering).
Canonical source: `docs/diagrams/src/canonical-coordinator-journey.drawio`.
Stable PNG: `docs/diagrams/canonical-coordinator-journey.png`.
Owner: shared-eight-survivors; claim is local because the assignment forbids inventory writes.
Documentation integration is owned by the parent; no Markdown consumer was edited here.

## Grounding
apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:65-82 and assembly workflow construction; apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; coordinator-provided completed assurance research summary

See `research-components.md`, `research-flows.md`, `research-assurance.md` for the three
completed independent-thread handoffs supplied by the coordinator. These are supplied-summary
records, not verbatim copies of inaccessible Temp attachments. Direct implementation reads
resolved the board's actual stage mapping and verified EF's durable relay and project references.
`content-model.json` records every semantic node/participant and its classification.
Legacy diagrams were used only to preserve identity, not as authority for runtime claims.

## Actual PNG inspection and handoff
Opened the exported pitch PNG at enlarged resolution and its actual print-scale raster on
`../canonical-agent-communication-handoff/pitch-print-sheet-1.png` or `-2.png`.
The pitch is a deliberately small editable concept sketch, not a publication.
Visible risks handed to pass 1: sparse hierarchy, long or crossing labels, absent native
symbols and omitted detailed boundaries/assurance. Sequence pitches retain lifelines.
The email architecture pitch's long A2A edge crosses the middle card: explicitly not clean.
All added pass-1 content must remain grounded in the evidence above.

## Visual references and assets
- Started from `docs/diagrams/drawio/fluent-template.drawio`; loaded `fluent-library.xml`.
  Removed the invisible template metadata and all example cells; set one editable 827 x 583 A5 landscape page.
- Palette, warm surfaces, Segoe UI, 16 px rounded cards, 5 px accents, tiered groups,
  title/subtitle/metadata/pill hierarchy derive from `drawio/design-system.json`.
- React reference: `apps/web/src/components/CoordinatorTopologyGraph.tsx:93-130`
  (Fluent neutral surfaces, border hierarchy, marigold state treatment and badges).
- Native geometry is bundled draw.io UML/flowchart/database/cloud/Kubernetes notation.
  draw.io source: https://github.com/jgraph/drawio (Apache-2.0; bundled third-party assets retain their notices).
- Entra uses the native bundled Azure identity SVG, not a hand-drawn logo:
  `img/lib/azure2/identity/Azure_Active_Directory.svg`.
  Microsoft architecture-icon usage guidance: https://learn.microsoft.com/azure/architecture/icons/.
  No downloaded brand imagery or embedded raster padding was used.
- Product-only concepts use custom Agentweaver hexagon badges in Fluent card chrome.

## Symbol inventory for the upgrade
- `intent`: custom:agentweaver — Confirm intent.
- `plan`: custom:agentweaver — Plan the work.
- `dispatch`: custom:agentweaver — Dispatch children.
- `finish`: custom:agentweaver — Merge + Scribe.
- `review`: native:flowchart — Collective review.
- `integrate`: custom:agentweaver — Integrate + gates.
