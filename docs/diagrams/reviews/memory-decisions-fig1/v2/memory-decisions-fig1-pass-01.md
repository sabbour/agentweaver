# From proposals to usable context - pass-01

Verified authorship and trust gates control selection; selected content remains untrusted data.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Resolve authorship: Human or verified run; exact project scope | `apps/Agentweaver.Api/Security/RunAuthorship.cs:40-89` |
| n1 | Pending inbox: Persist source provenance; decision proposal | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:49-152` |
| n2 | Pending memory: Validate type and importance; source + pending trust | `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:125-160` |
| n3 | Authorized promotion: Owner / verified Coordinator; transactional decision | `apps/Agentweaver.Api/Memory/DecisionPromotion.cs:22-55` |
| n4 | Authorized rejection: Retain rejected inbox row; history is not deletion | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:285-301` |
| n5 | Scoped Scribe path: Only eligible low-risk entries; same run + time window | `apps/Agentweaver.Api/Runs/PostRunScribeService.cs:42-104` |
| n6 | Approved boundaries: Active architecture / scope; approved trust required | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:58-65` |
| n7 | Memory + session: Non-legacy; cross-team gated; bounded memory selection | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:68-140` |
| n8 | Untrusted JSON: Eligibility is not authority; data, not instructions | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:175-217` |

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
  "baseline_meaningful_xml": 1526,
  "result_meaningful_xml": 23454,
  "baseline_visible_structures": 6,
  "result_visible_structures": 81,
  "growth_ratio": 15.369594,
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
| e0 | n0 -> n1 | Verified authorship is persisted on inbox proposals | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:49-152` |
| e1 | n0 -> n2 | Verified authorship accompanies pending memory | `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:125-160` |
| e2 | n1 -> n3 | Authorized decision promotion is transactional | `apps/Agentweaver.Api/Memory/DecisionPromotion.cs:22-55` |
| e3 | n1 -> n4 | Authorized rejection preserves the row | `apps/Agentweaver.Api/Endpoints/DecisionsEndpoints.cs:285-301` |
| e4 | n1 -> n5 | Only scoped low-risk inbox entries reach Scribe | `apps/Agentweaver.Api/Runs/PostRunScribeService.cs:42-104` |
| e5 | n3 -> n6 | Compiler selects approved active architecture/scope decisions | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:58-65` |
| e6 | n2 -> n7 | Memory must pass ownership, trust and budget filters | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:68-140` |
| e7 | n6 -> n8 | Selected decisions become untrusted JSON data | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:175-217` |
| e8 | n7 -> n8 | Selected memories and current session become untrusted data | `apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:175-217` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
