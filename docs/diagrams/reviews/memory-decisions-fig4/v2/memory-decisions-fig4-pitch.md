# One authority, asymmetric exchange - pitch

The store exports several views; only inbox Markdown imports as pending proposals.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Memory store: Provider-neutral authority; SQLite: memory.db | `apps/Agentweaver.Api/Memory/MemoryLedgerExporter.cs:37-83` |
| n1 | Approved decisions: Active decisions only; decisions.md | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:47-66` |
| n2 | Approved boundaries: Architecture / scope only; boundaries.md | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:119-139` |
| n3 | Pending inbox files: Rewrite current pending set; decisions/inbox/*.md | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:68-80` |
| n4 | Agent history: Eligible learning / updates; agent history.md | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:84-105` |
| n5 | Current session: Latest open session only; identity/now.md | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:108-116` |
| n6 | Approved patterns: Pattern memories only; patterns.md | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:142-157` |
| n7 | Inbox parser: Parse; skip malformed files; not a general file sync | `packages/Agentweaver.Squad/Memory/SquadMemoryImporter.cs:20-71` |
| n8 | Missing-slug proposal: Existing slugs stay intact; pending, not approved | `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:528-548` |

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
