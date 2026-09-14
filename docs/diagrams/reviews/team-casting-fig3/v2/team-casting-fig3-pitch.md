# Confirm a team against its revision - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
