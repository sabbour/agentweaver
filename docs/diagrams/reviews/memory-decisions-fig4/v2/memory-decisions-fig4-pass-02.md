# One authority, asymmetric exchange - pass-02

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


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Export active approved decisions | `apps/Agentweaver.Api/Memory/MemoryLedgerExporter.cs:45-51` |
| e1 | n0 -> n2 | Export approved architectural/scope boundaries | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:119-139` |
| e2 | n0 -> n3 | Export pending inbox entries | `apps/Agentweaver.Api/Memory/MemoryLedgerExporter.cs:53-55` |
| e3 | n0 -> n4 | Export eligible non-legacy history entries | `apps/Agentweaver.Api/Memory/MemoryLedgerExporter.cs:57-63` |
| e4 | n0 -> n5 | Export latest open session if present | `apps/Agentweaver.Api/Memory/MemoryLedgerExporter.cs:66-71` |
| e5 | n0 -> n6 | Export approved pattern memories | `packages/Agentweaver.Squad/Memory/SquadMemoryExporter.cs:142-157` |
| e6 | n3 -> n7 | Only inbox Markdown enters the importer | `packages/Agentweaver.Squad/Memory/SquadMemoryImporter.cs:20-71` |
| e7 | n7 -> n8 | Insert only missing slugs as pending proposals | `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:528-548` |
| e8 | n8 -> n0 | Imported proposals remain subject to approval | `apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:538-543` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven print sheets and fourteen original-pixel pairs. The Review API heading collision and resilient budget title wrap are corrected. Existing content and Fluent styling remain intact; residual close-lane issues are recorded per diagram.

Correction-only pass: no added content or reopened composition. Existing-label fitting, native segment-routing correction and any heading repair are recorded in corrections-pass-02.json.
