# email-architecture: pitch

Audience: Agentweaver users and architecture readers.
Takeaway: Entra authenticates people; GitHub capabilities authorize purpose-bound repository access.
Orientation: A5-landscape, one uncompressed editable 827 x 583 page.
Disposition: retain (retain preserves supported identity, not the old rendering).
Canonical source: `docs/diagrams/src/email-architecture.drawio`.
Stable PNG: `docs/diagrams/email-architecture.png`.
Owner: shared-eight-survivors; claim is local because the assignment forbids inventory writes.
Documentation integration is owned by the parent; no Markdown consumer was edited here.

## Grounding
k8s/base/api-deployment.yaml:53-76,337-349; k8s/base/worker-deployment.yaml:54-76,146-170; k8s/base/mcp-deployment.yaml:21; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:10-36; apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs; coordinator-provided component and assurance findings

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
- `api`: native:kubernetes — API + worker.
- `mcp`: native:kubernetes — MCP service.
- `sandbox`: native:kubernetes — AgentHost pod.
- `entra`: native:azure — Microsoft Entra.
- `github`: native:cloud — GitHub.
- `postgres`: native:database — PostgreSQL.
