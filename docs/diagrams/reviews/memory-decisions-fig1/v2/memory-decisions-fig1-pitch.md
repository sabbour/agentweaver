# From proposals to usable context - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
