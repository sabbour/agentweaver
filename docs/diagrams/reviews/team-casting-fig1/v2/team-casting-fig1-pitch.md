# Compile a proposal before writing a team - pitch

Proposal generation may persist a draft; .squad writes wait for guarded confirmation.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Intent -> known roles: Scenario, manual or model; catalog role resolution | `apps/Agentweaver.Api/Casting/CastingService.cs:125-283` |
| n1 | Policy + history: Current team and registry; naming context | `apps/Agentweaver.Api/Casting/CastingService.cs:150-173` |
| n2 | Choose universe: Policy, override and seed; not repository signals | `packages/Agentweaver.Squad/Naming/UniverseAllocator.cs:23-52` |
| n3 | Allocate names: Reserve names case-insensitively; member-N overflow | `packages/Agentweaver.Squad/Naming/UniverseAllocator.cs:59-106` |
| n4 | Compile charters: Trusted role metadata; deterministic compiler | `packages/Agentweaver.Squad/Squad/CharterCompiler.cs:21-51` |
| n5 | Pending proposal: Roster + charters + revision; short-lived proposal store | `apps/Agentweaver.Api/Casting/CastingService.cs:219-232` |
| n6 | Reject or expire: Remove pending proposal; no .squad mutation | `apps/Agentweaver.Api/Casting/CastingService.cs:889-894` |
| n7 | Guarded confirmation: Check current team revision; new / augment / recast | `apps/Agentweaver.Api/Casting/CastingService.cs:899-1033` |
| n8 | Persist team + extras: Files/events, then best effort; seed / .squad commit | `apps/Agentweaver.Api/Casting/CastingService.cs:1042-1208` |

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
