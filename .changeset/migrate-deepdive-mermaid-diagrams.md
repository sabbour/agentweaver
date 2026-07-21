---
"agentweaver": patch
---

Migrated the `docs/deep-dive/*.md` Mermaid **flowcharts** onto the Fluent-styled
`@xyflow/react` + `dagre` diagram pipeline (the same one used for the AKS
architecture diagrams), so they render as on-brand node/card diagrams with the
overlap-free edge routing shipped previously, instead of raw ```mermaid fences.

Adds a reusable converter (`scripts/docs/mermaid-to-graphspec.mjs`) and a
migration CLI (`scripts/docs/migrate-mermaid.mjs`) that lift the semantics the
Mermaid sources already carry — `class` category assignments, node shapes, and
nested `subgraph` clusters — into graph-spec card icons/badges and groups. 104
flowcharts across 36 deep-dive docs were converted to `docs/diagrams/src/*.json`
specs and pre-rendered to PNG. Non-flowchart Mermaid (`sequenceDiagram`,
`stateDiagram`, `classDiagram`, `erDiagram`) is intentionally left as-is — it is
not representable by the node/edge/group graph-spec and keeps rendering via
`vitepress-plugin-mermaid` (tracked as follow-up).
