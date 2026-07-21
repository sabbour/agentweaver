---
"agentweaver": patch
---

Migrated the Mermaid **flowcharts** in `docs/guide/*.md`, `docs/reference/*.md`
and `docs/run-event-stream.md` onto the Fluent-styled `@xyflow/react` + `dagre`
diagram pipeline (Phase 2, Batch C), replacing raw ```mermaid fences with
pre-rendered PNG embeds. 19 flowcharts across those docs were converted to
`docs/diagrams/src/*.json` specs and pre-rendered to PNG (the 3 hand-authored
AKS architecture PNGs already embedded in `docs/guide/architecture-aks.md` are
untouched). Non-flowchart Mermaid (`sequenceDiagram`) is intentionally left as-is
and keeps rendering via `vitepress-plugin-mermaid`.

Fixes `migrate-mermaid.mjs` to compute the diagram embed path relative to each
doc's directory, so a doc at the `docs/` root (like `run-event-stream.md`)
correctly references `diagrams/…` instead of `../diagrams/…`.
