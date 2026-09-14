# Allocate an inbox slug safely - pass-01

Update only a matching pending author; numbered candidates must be checked again.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Verified submission: Project + requested slug; authorized author | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:49-70` |
| n1 | Requested slug exists?: Look up within project; project / slug | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:64-77` |
| n2 | Same-agent terminal?: Merged or rejected replay; explicit conflict | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:76-79` |
| n3 | Pending match?: Same agent, kind, identity; SourceKind + identity | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:81-103` |
| n4 | Update existing: Preserve proposal identity; idempotent pending edit | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:81-103` |
| n5 | Allocate candidate: Slug plus agent segment; slug--agent | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:114-116` |
| n6 | Candidate available?: Check every numbered slug; repeat the lookup | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:117-126` |
| n7 | Increment suffix: Try the next candidate; --2, --3, ... | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:117-126` |
| n8 | Insert pending: Unique project/slug backstop; racing insert may fail | `apps/Agentweaver.Api.Data/Memory/MemoryDbContext.cs:93-94` |

## Symbols and credits

Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.
Process, decision, event and document icons are native:flowchart; cylinders native:database;
people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.
Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.
Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,
fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.

## Meaningful expansion

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 1545,
  "result_meaningful_xml": 24241,
  "baseline_visible_structures": 6,
  "result_visible_structures": 83,
  "growth_ratio": 15.689968,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true
}
```

## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Look up requested project-scoped slug | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:49-70` |
| e1 | n1 -> n8 | Unused requested slug creates a pending row | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:130-152` |
| e2 | n1 -> n2 | Existing slug checks terminal replay first | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:76-79` |
| e3 | n2 -> n3 | Pending existing row checks full provenance match | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:81-103` |
| e4 | n3 -> n4 | Matching pending author and provenance updates existing row | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:81-103` |
| e5 | n3 -> n5 | Different pending provenance allocates a new slug | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:114-116` |
| e6 | n2 -> n5 | Different agent allocates a distinct candidate | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:76-116` |
| e7 | n5 -> n6 | Check candidate availability | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:117-126` |
| e8 | n6 -> n7 | Occupied candidate increments suffix | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:117-126` |
| e9 | n7 -> n6 | Numbered candidate loops back through availability check | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:117-126` |
| e10 | n6 -> n8 | Free candidate is inserted; database enforces uniqueness | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:130-152` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
