# canonical-durable-event-stream: pitch

Audience: Agentweaver users and architecture readers.
Takeaway: Any API replica can serve a cursor over durable RunEvents—no sticky session required.
Orientation: A5-landscape, one uncompressed editable 827 x 583 page.
Disposition: redesign (retain preserves supported identity, not the old rendering).
Canonical source: `docs/diagrams/src/canonical-durable-event-stream.drawio`.
Stable PNG: `docs/diagrams/canonical-durable-event-stream.png`.
Owner: shared-eight-survivors; claim is local because the assignment forbids inventory writes.
Documentation integration is owned by the parent; no Markdown consumer was edited here.

## Grounding
apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15-36,66-87,145-190,220-320; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs

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
- `producer`: custom:agentweaver — Run producer.
- `append`: native:uml — EF event stream.
- `store`: native:database — RunEvents.
- `client`: native:uml — Web / MCP watcher.
- `sse`: native:uml — SSE endpoint.
- `reader`: native:uml — EF subscriber.
