# Iteration review checklist

Complete every row for every pass and save a distinct `.drawio`, PNG, and change record.

## Scope

- Documentation artifacts only; shipped product surfaces are read-only references.
- Do not alter product topology/workflow UI, routing helpers, runtime APIs, or behavior.
- Do not change `packages/Agentweaver.Squad` workflow definitions.
- Keep root dependencies required by non-documentation tooling.

## Batch ownership

- Select diagrams by canonical name or one inventory `owner_area`.
- Preserve `docs/diagrams/<name>.png` for every redesign.
- Do not change another area's inventory entries.
- Keep one active writer for each source, PNG, review directory, and inventory entry.
- Use repeated `--spec <name>` arguments for selective render/check batches.

| Check | Pass 1 | Pass 2 | Pass 3 | Pass 4+ |
| --- | --- | --- | --- | --- |
| Inspect input PNG | required | required | required | required |
| Permitted work | visual upgrade | corrections only | corrections only | corrections only |
| Inspect output PNG at A5 print size | required | required | required | required |
| Inspect output PNG enlarged | required | required | required | required |
| Orientation review | required | required | required | required |
| Overlap review | required | required | required | required |
| Arrow direction/endpoints/routing review | required | required | required | required |
| Meaningful XML >=9x pitch | required | preserve | preserve | preserve |
| Trace every arrow | recommended | affected arrows | affected arrows | required on pass 4 and final pass |

## Pass-1 anti-padding audit

- No invisible or fully transparent vertices/edges.
- No off-page objects used only to increase XML size.
- No duplicate/redundant cells or metadata padding.
- No comments, whitespace, or embedded image bytes counted toward growth.
- Growth uses the `visible-semantic-canonical-xml-v1` metric.
- New detail is visible, source-backed, and readable on one A5 page.

## Agentweaver style audit

- Warm neutral canvas, surfaces, ink, and strokes.
- Segoe UI typography; monospace only for metadata.
- Rounded cards, restrained shadows, and 5 px semantic accent.
- Icon/title/subtitle/metadata/badge hierarchy.
- Tiered groups with readable titles.
- Native Azure, Kubernetes, C4, UML, flowchart/BPMN, networking, database, and cloud symbols where correct.
- Agentweaver custom components only for product-specific concepts or library gaps.
- Orthogonal rounded connectors, packed lanes, label backgrounds, real bridges, and genuine split/merge dots.
- Dashed marigold outer rails only for semantic revision/return flow.

## Arrow trace fields

For each connector record:

```text
id | source | target | relationship | evidence | direction | endpoints | route | result
```
