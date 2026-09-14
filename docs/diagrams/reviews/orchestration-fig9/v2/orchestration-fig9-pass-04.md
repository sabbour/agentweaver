# Execution and observation cooperate - pass-04

The watcher projects runtime events into durable state; it is not the executing graph.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Run orchestrator: Starts factory + watcher; supervised lifetime | `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:260-364` |
| n1 | Effective definition: Resolve concrete workflow; no policy composer | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1495-1517` |
| n2 | Factory + binder: Build executable graph; typed bindings | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| n3 | Checkpointed stream: MAF executes the graph; provider-aware store | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| n4 | Watch loop: Consumes runtime updates; not graph execution | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-335` |
| n5 | Review request: Persist pending decision; durable pause context | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:363-377` |
| n6 | Typed terminal: Classify completed output; not inferred from text | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-700` |
| n7 | Durable run state: Persist status projection; watcher owns updates | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-700` |
| n8 | Workflow-step events: Expose execution progress; client observation | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-409` |

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
| e0 | n0 -> n2 | Orchestrator starts workflow factory | `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:960` |
| e1 | n1 -> n2 | Factory resolves and binds effective definition | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1517` |
| e2 | n2 -> n3 | Factory starts checkpointed execution stream | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| e3 | n0 -> n4 | Orchestrator supervises the watcher | `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:260-364` |
| e4 | n3 -> n4 | Watcher consumes runtime stream updates | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-335` |
| e5 | n4 -> n5 | Review request persists pending state | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:363-377` |
| e6 | n4 -> n6 | Watcher classifies typed terminal output | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-700` |
| e7 | n6 -> n7 | Terminal classification updates durable run state | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:595-700` |
| e8 | n4 -> n8 | Watcher exposes workflow progress events | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-409` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
