# Collective assembly and review - pitch

RED parks durably for a human. REVISE enters explicit steering, not RaiBlocked.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Claim + eligibility: No partial failed plan; awaiting -> assembling | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:851-913` |
| n1 | Integration snapshot: Ordered child branches; branch / tree / diff | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:910-981` |
| n2 | Applicable gates: Workflow-defined ordering; non-code: omit build | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1586-1676` |
| n3 | Gate outcomes: Pass: next; REVISE: steer; RAI RED: human park | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1109-1194` |
| n4 | Normal human gate: Persist request, then wait; approve: next gates | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1198-1464` |
| n5 | Safety / budget park: Durable human escalation; in_review / awaiting | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2845-2958` |
| n6 | Explicit steering: In-place, fresh or advisory; Proceed: human park | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2232-2397` |
| n7 | Recovered review: Use saved branch and tree; no routine rebuild | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1321-1409` |
| n8 | Approved completion: Lock, merge, then Scribe; Scribe error: nonfatal | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1678-1852` |

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
