# canonical-default-workflow — pass-04

Actual exported PNG opened with the image-view tool, together with its 583 × 827 print-size derivative. Both views were inspected in this session; no XML-only or inferred image certification.

## Observations

Final correction-only pass saved a distinct source and fresh export. Both the actual PNG and print derivative were opened and inspected. Traced all 12 arrows individually from source through every bend/crossing to the intended target arrowhead, then compared all source/target/verdict tuples with current YAML or implementation. No label collision, false junction, off-page content, native glyph clipping, wrong endpoint or direction remains. No further edit was needed after pass 3.

Remaining orientation defects: 0. Remaining overlap/routing defect groups: 0.

## Contract and evidence

One true A5 portrait page, 583 × 827 draw.io units, pageScale=1. Export: draw.io 31.4.5, border 16, scale 2. Warm canvas, near-white 16-unit rounded cards, Segoe UI, shadows, 5-unit semantic accents, native glyphs, title/subtitle/metadata/pills and group hierarchy.

Evidence model and all node/edge source citations: `evidence.json`. Independent research: `../../canonical-provider-admission/research-thread-01.md`, `-02.md`, `-03.md`. Native diagrams.net flowchart process/decision/document/terminal symbols (Apache-2.0); no third-party image assets. Fluent styling derives from repository template/library/design-system and WorkflowGraphPanel; workflow-authoring a5-final was read-only inspiration.


## Complete visual arrow trace

Each row was visually followed on the final PNG, not merely inferred from the XML. Ports and waypoints below are the editable-source audit of that observed route. Crossings retain native jump arcs; no logical junction dots are introduced.

| ID | Direction | Verdict | Endpoints | Route | Visual result |
|---|---|---|---|---|---|
| edge-01 | agent → rai | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-02 | rai → agent | revise | exit (0,0.8) → entry (0.11162790697674418,1) | marigold dashed return; (174,254.6); (174,277); (12,277); (12,177); (209,177) | clean |
| edge-03 | rai → terminal-safety-failed | safety-failed | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (446,220.5); (446,333) | clean |
| edge-04 | rai → scribe | no-changes | exit (1,0.7) → entry (1,0.3333333333333333) | solid orthogonal advance/outcome; (450,248.4); (450,625.667) | clean |
| edge-05 | rai → review | review | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-06 | review → merge | approved | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-07 | review → agent | request-changes | exit (0,0.8) → entry (0.19534883720930232,1) | marigold dashed return; (174,354.6); (174,377); (20,377); (20,179.5); (227,179.5) | clean |
| edge-08 | review → terminal-declined | declined | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (454,320.5); (454,553) | clean |
| edge-09 | merge → push-pr | merged | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-10 | merge → review | blocked | exit (0,0.8) → entry (0.27906976744186046,1) | marigold dashed return; (174,454.6); (174,477); (28,477); (28,392); (245,392) | clean |
| edge-11 | push-pr → scribe | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-12 | scribe → done | unconditional | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (446,620.5); (446,753) | clean |

### Source evidence per traced arrow

- `edge-01`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:120–121`.
- `edge-02`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:124–126`.
- `edge-03`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:127–129`.
- `edge-04`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:130–132`.
- `edge-05`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:133–135`.
- `edge-06`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:138–140`.
- `edge-07`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:141–143`.
- `edge-08`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:144–146`.
- `edge-09`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:149–151`.
- `edge-10`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:152–154`.
- `edge-11`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:157–158`.
- `edge-12`: `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:159–160`.
