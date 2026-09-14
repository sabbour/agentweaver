# Allocate an inbox slug safely - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
