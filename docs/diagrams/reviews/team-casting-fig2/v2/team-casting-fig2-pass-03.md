# Analyze summaries, not raw source - pass-03

The model sees bounded signals and a role menu; recognized roles become a deterministic proposal.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Repository: Local manifests and filenames; not model-bound source | `packages/Agentweaver.Squad/Analysis/ProjectSignalScanner.cs:14-53` |
| n1 | Bounded scanner: Skip excluded/reparse paths; 500-file cap | `packages/Agentweaver.Squad/Analysis/ProjectSignalScanner.cs:127-175` |
| n2 | Signal summary: Languages, tests, docs, CI; framework + size signals | `packages/Agentweaver.Squad/Analysis/ProjectSignalScanner.cs:55-124` |
| n3 | Catalog role menu: Known role IDs only; trusted catalog metadata | `apps/Agentweaver.Api/Casting/CastingService.cs:511-530` |
| n4 | Analysis prompt: Summary is fenced data; no signals: warning | `apps/Agentweaver.Api/Casting/CastingService.cs:511-535` |
| n5 | Parse response: Malformed output fails; role selections | `apps/Agentweaver.Api/Casting/CastingService.cs:614-623` |
| n6 | Resolve catalog IDs: Skip unknown role IDs; none recognized: fail | `apps/Agentweaver.Api/Casting/CastingService.cs:625-645` |
| n7 | Names + charters: Universe chosen independently; deterministic compilation | `apps/Agentweaver.Api/Casting/CastingService.cs:647-689` |
| n8 | Stored proposal: No team file writes yet; pending confirmation | `apps/Agentweaver.Api/Casting/CastingService.cs:647-689` |

## Symbols and credits

Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.
Process, decision, event and document icons are native:flowchart; cylinders native:database;
people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.
Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.
Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,
fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Scan bounded local repository inputs | `packages/Agentweaver.Squad/Analysis/ProjectSignalScanner.cs:14-175` |
| e1 | n1 -> n2 | Convert detected signals into text summary | `packages/Agentweaver.Squad/Analysis/ProjectSignalScanner.cs:55-124` |
| e2 | n2 -> n4 | Supply signal summary, not raw repository source | `apps/Agentweaver.Api/Casting/CastingService.cs:511-530` |
| e3 | n3 -> n4 | Supply catalog role menu to constrained analysis | `apps/Agentweaver.Api/Casting/CastingService.cs:511-530` |
| e4 | n4 -> n5 | Parse model role-selection output | `apps/Agentweaver.Api/Casting/CastingService.cs:614-623` |
| e5 | n5 -> n6 | Resolve returned IDs against known role catalog | `apps/Agentweaver.Api/Casting/CastingService.cs:625-645` |
| e6 | n6 -> n7 | Recognized roles feed deterministic proposal compilation | `apps/Agentweaver.Api/Casting/CastingService.cs:647-689` |
| e7 | n7 -> n8 | Store compiled proposal without team mutation | `apps/Agentweaver.Api/Casting/CastingService.cs:647-689` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened seven A5-approximation sheets and fourteen original-pixel pairs, plus individual dense collective and binding exports. Confirmed the near-parallel hairpin and pass/RED label association are corrected, title fits remain intact, and arrows clear cards/group headings. No remaining orientation, overlap or routing defect observed.

Correction-only pass: no added content or reopened composition. Route-clearance and label-association repairs are recorded in corrections-pass-03.json; all vertices and relationships are preserved.
