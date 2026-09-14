# canonical-workflow-selection — pass-04

Actual exported PNG opened with the image-view tool, together with its 583 × 827 print-size derivative. Both views were inspected in this session; no XML-only or inferred image certification.

## Observations

Final correction-only pass saved a distinct source and fresh export. Both the actual PNG and print derivative were opened and inspected. Traced all 14 arrows individually from source through every bend/crossing to the intended target arrowhead, then compared all source/target/verdict tuples with current YAML or implementation. No label collision, false junction, off-page content, native glyph clipping, wrong endpoint or direction remains. No further edit was needed after pass 3.

Remaining orientation defects: 0. Remaining overlap/routing defect groups: 0.

## Contract and evidence

One true A5 portrait page, 583 × 827 draw.io units, pageScale=1. Export: draw.io 31.4.5, border 16, scale 2. Warm canvas, near-white 16-unit rounded cards, Segoe UI, shadows, 5-unit semantic accents, native glyphs, title/subtitle/metadata/pills and group hierarchy.

Evidence model and all node/edge source citations: `evidence.json`. Independent research: `../../canonical-provider-admission/research-thread-01.md`, `-02.md`, `-03.md`. Native diagrams.net flowchart process/decision/document/terminal symbols (Apache-2.0); no third-party image assets. Fluent styling derives from repository template/library/design-system and WorkflowGraphPanel; workflow-authoring a5-final was read-only inspiration.


## Complete visual arrow trace

Each row was visually followed on the final PNG, not merely inferred from the XML. Ports and waypoints below are the editable-source audit of that observed route. Crossings retain native jump arcs; no logical junction dots are introduced.

| ID | Direction | Verdict | Endpoints | Route | Visual result |
|---|---|---|---|---|---|
| edge-01 | available → explicit | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-02 | explicit → explicit-result | available | exit (1,0.25) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (446,220.5); (446,234.667) | clean |
| edge-03 | explicit → conversation | absent / invalid | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-04 | conversation → explicit-result | available | exit (1,0.25) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (446,320.5); (446,259.333) | clean |
| edge-05 | conversation → count | absent / invalid | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-06 | count → silent | 0 or 1 | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (446,420.5); (446,440) | clean |
| edge-07 | count → model | 2+ | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-08 | model → validate | response | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-09 | model → fallback | exception | exit (1,0.25) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (446,520.5); (446,632.333) | clean |
| edge-10 | validate → selected | accepted | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-11 | validate → model | retry once | exit (0,0.8) → entry (0.11162790697674418,1) | marigold dashed return; (174,654.6); (174,677); (12,677); (12,577); (209,577) | clean |
| edge-12 | validate → fallback | 2 unusable | exit (1,0.25) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (450,620.5); (450,660.667) | clean |
| edge-13 | fallback → selected | emit choice | exit (0.5,1) → entry (1,0.7) | solid orthogonal advance/outcome; (517,790); (436,790); (436,748.4) | clean |
| edge-14 | available → outer | outer catch | exit (1,0.15) → entry (0,0.5) | solid orthogonal advance/outcome; (447,114.3); (447,145) | clean |

### Source evidence per traced arrow

- `edge-01`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-02`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-03`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-04`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-05`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-06`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-07`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-08`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-09`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-10`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-11`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-12`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-13`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
- `edge-14`: `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:271–405; apps/Agentweaver.Api/Coordinator/WorkflowSelector.cs:93–215`.
