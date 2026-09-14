# workflow-infra-ops — pass-04

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
| edge-01 | plan → implement | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-02 | implement → validate-gate | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-03 | validate-gate → rai-check | pass | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-04 | validate-gate → implement | fail | exit (0,0.8) → entry (0.11162790697674418,1) | marigold dashed return; (174,354.6); (174,377); (12,377); (12,277); (209,277) | clean |
| edge-05 | rai-check → implement | revise | exit (0,0.8) → entry (0.19534883720930232,1) | marigold dashed return; (174,454.6); (174,477); (20,477); (20,279.5); (227,279.5) | clean |
| edge-06 | rai-check → terminal-safety-failed | safety-failed | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (446,420.5); (446,333) | clean |
| edge-07 | rai-check → done | no-changes | exit (1,0.7) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (446,448.4); (446,742) | clean |
| edge-08 | rai-check → infra-review | review | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-09 | infra-review → human-review | approved | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-10 | infra-review → implement | request-changes | exit (0,0.8) → entry (0.27906976744186046,1) | marigold dashed return; (174,554.6); (174,577); (28,577); (28,282); (245,282) | clean |
| edge-11 | infra-review → terminal-declined | declined | exit (1,0.25) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (450,520.5); (450,542) | clean |
| edge-12 | human-review → done | approved | exit (1,0.25) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (454,620.5); (454,764) | clean |
| edge-13 | human-review → implement | request-changes | exit (0,0.8) → entry (0.3627906976744186,1) | marigold dashed return; (174,654.6); (174,677); (36,677); (36,284.5); (263,284.5) | clean |
| edge-14 | human-review → terminal-declined | declined | exit (1,0.7) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (450,648.4); (450,564) | clean |

### Source evidence per traced arrow

- `edge-01`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:92–93`.
- `edge-02`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:95–96`.
- `edge-03`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:99–101`.
- `edge-04`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:103–105`.
- `edge-05`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:108–110`.
- `edge-06`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:112–114`.
- `edge-07`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:116–118`.
- `edge-08`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:120–122`.
- `edge-09`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:125–127`.
- `edge-10`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:129–131`.
- `edge-11`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:133–135`.
- `edge-12`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:138–140`.
- `edge-13`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:142–144`.
- `edge-14`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/infra_ops.yaml:146–148`.
