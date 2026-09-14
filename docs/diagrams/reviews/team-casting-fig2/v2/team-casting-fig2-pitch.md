# Analyze summaries, not raw source - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
