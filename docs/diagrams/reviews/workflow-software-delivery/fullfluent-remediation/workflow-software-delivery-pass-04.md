# workflow-software-delivery — pass-04

Actual exported PNG opened with the image-view tool, together with its 583 × 827 print-size derivative. Both views were inspected in this session; no XML-only or inferred image certification.

## Observations

Final correction-only pass saved a distinct source and fresh export. Both the actual PNG and print derivative were opened and inspected. Traced all 17 arrows individually from source through every bend/crossing to the intended target arrowhead, then compared all source/target/verdict tuples with current YAML or implementation. No label collision, false junction, off-page content, native glyph clipping, wrong endpoint or direction remains. No further edit was needed after pass 3.

Remaining orientation defects: 0. Remaining overlap/routing defect groups: 0.

## Contract and evidence

One true A5 portrait page, 583 × 827 draw.io units, pageScale=1. Export: draw.io 31.4.5, border 16, scale 2. Warm canvas, near-white 16-unit rounded cards, Segoe UI, shadows, 5-unit semantic accents, native glyphs, title/subtitle/metadata/pills and group hierarchy.

Evidence model and all node/edge source citations: `evidence.json`. Independent research: `../../canonical-provider-admission/research-thread-01.md`, `-02.md`, `-03.md`. Native diagrams.net flowchart process/decision/document/terminal symbols (Apache-2.0); no third-party image assets. Fluent styling derives from repository template/library/design-system and WorkflowGraphPanel; workflow-authoring a5-final was read-only inspiration.


## Complete visual arrow trace

Each row was visually followed on the final PNG, not merely inferred from the XML. Ports and waypoints below are the editable-source audit of that observed route. Crossings retain native jump arcs; no logical junction dots are introduced.

| ID | Direction | Verdict | Endpoints | Route | Visual result |
|---|---|---|---|---|---|
| edge-01 | plan → implement | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-02 | implement → test-gate | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-03 | test-gate → rai-check | pass | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-04 | test-gate → implement | fail | exit (0,0.8) → entry (0.11162790697674418,1) | marigold dashed return; (174,328.886); (174,351.286); (12,351.286); (12,264.143); (209,264.143) | clean |
| edge-05 | rai-check → implement | revise | exit (0,0.8) → entry (0.19534883720930232,1) | marigold dashed return; (174,416.029); (174,438.429); (20,438.429); (20,266.643); (227,266.643) | clean |
| edge-06 | rai-check → terminal-safety-failed | safety-failed | exit (1,0.25) → entry (0,0.5) | solid orthogonal advance/outcome; (446,381.929); (446,333) | clean |
| edge-07 | rai-check → done | no-changes | exit (1,0.7) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (446,409.829); (446,742) | clean |
| edge-08 | rai-check → rubberduck | review | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-09 | rubberduck → code-review | pass | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-10 | rubberduck → implement | revise | exit (0,0.8) → entry (0.27906976744186046,1) | marigold dashed return; (174,503.171); (174,525.571); (28,525.571); (28,269.143); (245,269.143) | clean |
| edge-11 | code-review → build-test | unconditional | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-12 | build-test → review-gate | approved | exit (0.5,1) → entry (0.5,0) | solid orthogonal advance/outcome; direct vertical gutter | clean |
| edge-13 | build-test → implement | request-changes | exit (0,0.8) → entry (0.3627906976744186,1) | marigold dashed return; (174,677.457); (174,699.857); (36,699.857); (36,271.643); (263,271.643) | clean |
| edge-14 | build-test → terminal-declined | declined | exit (1,0.25) → entry (0,0.3333333333333333) | solid orthogonal advance/outcome; (450,643.357); (450,542) | clean |
| edge-15 | review-gate → done | approved | exit (1,0.25) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (450,730.5); (450,764) | clean |
| edge-16 | review-gate → implement | request-changes | exit (0,0.8) → entry (0.44651162790697674,1) | marigold dashed return; (174,764.6); (174,787); (44,787); (44,274.143); (281,274.143) | clean |
| edge-17 | review-gate → terminal-declined | declined | exit (1,0.7) → entry (0,0.6666666666666666) | solid orthogonal advance/outcome; (454,758.4); (454,564) | clean |

### Source evidence per traced arrow

- `edge-01`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:100–101`.
- `edge-02`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:103–104`.
- `edge-03`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:107–109`.
- `edge-04`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:111–113`.
- `edge-05`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:116–118`.
- `edge-06`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:120–122`.
- `edge-07`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:124–126`.
- `edge-08`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:128–130`.
- `edge-09`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:132–134`.
- `edge-10`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:136–138`.
- `edge-11`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:141–142`.
- `edge-12`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:145–147`.
- `edge-13`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:149–151`.
- `edge-14`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:153–155`.
- `edge-15`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:158–160`.
- `edge-16`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:162–164`.
- `edge-17`: `packages/Agentweaver.Squad/Catalog/Resources/workflows/software_delivery.yaml:166–168`.
