# Confirm a team against its revision - pass-03

Validate proposal and team revision before writes; later side effects are ordered, not atomic.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Live proposal?: Missing or expired exits; proposal ID | `apps/Agentweaver.Api/Casting/CastingService.cs:905-907` |
| n1 | Resolve intent: Existing-team choice matters; new / augment / recast | `apps/Agentweaver.Api/Casting/CastingService.cs:911-922` |
| n2 | Revision lease: Captured TeamRevision; conflict before writes | `apps/Agentweaver.Api/Casting/CastingService.cs:924-930` |
| n3 | Apply roster intent: Augment keeps; recast retires; explicit chosen semantics | `apps/Agentweaver.Api/Casting/CastingService.cs:946-1010` |
| n4 | Ensure built-ins: Scribe, Ralph, Rai, Coordinator; required built-in members | `apps/Agentweaver.Api/Casting/CastingService.cs:1015-1033` |
| n5 | Workspace files: Team, routing, charters, alumni; ordered filesystem writes | `apps/Agentweaver.Api/Casting/CastingService.cs:1042-1113` |
| n6 | Events + canonical state: Registry and history events; canonical JSON | `apps/Agentweaver.Api/Casting/CastingService.cs:1118-1181` |
| n7 | Complete lease: Remove pending proposal; after core writes | `apps/Agentweaver.Api/Casting/CastingService.cs:1182-1184` |
| n8 | Best-effort extras: Seed context; .squad commit; failures logged | `apps/Agentweaver.Api/Casting/CastingService.cs:1190-1207` |

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
| e0 | n0 -> n1 | Live proposal permits intent resolution | `apps/Agentweaver.Api/Casting/CastingService.cs:905-922` |
| e1 | n1 -> n2 | Acquire team mutation lease with captured revision | `apps/Agentweaver.Api/Casting/CastingService.cs:924-930` |
| e2 | n2 -> n3 | Only valid current team state permits roster writes | `apps/Agentweaver.Api/Casting/CastingService.cs:933-1010` |
| e3 | n3 -> n4 | Ensure required built-in roster members | `apps/Agentweaver.Api/Casting/CastingService.cs:1015-1033` |
| e4 | n4 -> n5 | Write workspace team/support/agent files | `apps/Agentweaver.Api/Casting/CastingService.cs:1042-1113` |
| e5 | n5 -> n6 | Record events and canonical state | `apps/Agentweaver.Api/Casting/CastingService.cs:1118-1181` |
| e6 | n6 -> n7 | Complete mutation lease and remove proposal | `apps/Agentweaver.Api/Casting/CastingService.cs:1182-1184` |
| e7 | n7 -> n8 | Run context seeding and .squad auto-commit afterward | `apps/Agentweaver.Api/Casting/CastingService.cs:1190-1207` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened seven A5-approximation sheets and fourteen original-pixel pairs, plus individual dense collective and binding exports. Confirmed the near-parallel hairpin and pass/RED label association are corrected, title fits remain intact, and arrows clear cards/group headings. No remaining orientation, overlap or routing defect observed.

Correction-only pass: no added content or reopened composition. Route-clearance and label-association repairs are recorded in corrections-pass-03.json; all vertices and relationships are preserved.
