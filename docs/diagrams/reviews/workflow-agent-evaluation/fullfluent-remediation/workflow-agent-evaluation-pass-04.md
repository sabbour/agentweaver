# workflow-agent-evaluation — pass-04

Actual exported PNG opened with the image-view tool, together with its 583 × 827 print-size derivative. Both views were inspected in this session; no XML-only or inferred image certification.

## Observations

Final correction-only pass saved a distinct source and fresh export. Both the actual PNG and print derivative were opened and inspected. Traced all 8 arrows individually from source through every bend/crossing to the intended target arrowhead, then compared all source/target/verdict tuples with current YAML or implementation. No label collision, false junction, off-page content, native glyph clipping, wrong endpoint or direction remains. No further edit was needed after pass 3.

Remaining orientation defects: 0. Remaining overlap/routing defect groups: 0.

## Contract and evidence

One true A5 portrait page, 583 × 827 draw.io units, pageScale=1. Export: draw.io 31.4.5, border 16, scale 2. Warm canvas, near-white 16-unit rounded cards, Segoe UI, shadows, 5-unit semantic accents, native glyphs, title/subtitle/metadata/pills and group hierarchy.

Evidence model and all node/edge source citations: `evidence.json`. Independent research: `../../canonical-provider-admission/research-thread-01.md`, `-02.md`, `-03.md`. Native diagrams.net flowchart process/decision/document/terminal symbols (Apache-2.0); no third-party image assets. Fluent styling derives from repository template/library/design-system and WorkflowGraphPanel; workflow-authoring a5-final was read-only inspiration.


## Complete visual arrow trace

Each row was visually followed on the final PNG, not merely inferred from the XML. Ports and waypoints below are the editable-source audit of that observed route. Crossings retain native jump arcs; no logical junction dots are introduced.

| ID | Direction | Verdict | Endpoints | Route | Visual result |
|---|---|---|---|---|---|
| edge-01 | eval-setup → eval-run | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-02 | eval-run → eval-collect | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-03 | eval-collect → safety-gate | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-04 | safety-gate → eval-setup | revise | exit (0,0.8) → entry (0.11162790697674418,1) | marigold dashed return; (174,454.6); (174,477); (12,477); (12,177); (209,177) | clean |
| edge-05 | safety-gate → terminal-safety-failed | safety-failed | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (446,420.5); (446,333) | clean |
| edge-06 | safety-gate → done | no-changes | exit (1,0.7) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (446,448.4); (446,742) | clean |
| edge-07 | safety-gate → report | review | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-08 | report → done | unconditional | exit (1,0.25) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (450,520.5); (450,764) | clean |

### Source evidence per traced arrow

- `edge-01`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:63–64`.
- `edge-02`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:66–67`.
- `edge-03`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:69–70`.
- `edge-04`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:73–75`.
- `edge-05`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:77–79`.
- `edge-06`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:81–83`.
- `edge-07`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:85–87`.
- `edge-08`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/agent_evaluation.yaml:89–90`.
