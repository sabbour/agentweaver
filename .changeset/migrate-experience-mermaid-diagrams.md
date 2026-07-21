---
"agentweaver": patch
---

Migrated the `docs/experience/*.md` Mermaid **flowcharts** onto the Fluent-styled
`@xyflow/react` + `dagre` diagram pipeline (Phase 2, Batch B), so they render as
on-brand node/card diagrams with the overlap-free edge routing instead of raw
```mermaid fences. 18 flowcharts across 14 experience docs were converted to
`docs/diagrams/src/*.json` specs and pre-rendered to PNG. Non-flowchart Mermaid
(`sequenceDiagram`, `stateDiagram`) is intentionally left as-is and keeps
rendering via `vitepress-plugin-mermaid`.

Hardens the shared converter/CLI in the process:

* `mermaid-to-graphspec.mjs` no longer splits node labels on `&` when it begins
  an HTML entity (`&gt;`, `&amp;`, `&#39;`, …); previously a label like
  `allow replicas &gt; 1` was torn apart and spawned a stray `gt` node.
* `migrate-mermaid.mjs` now names specs directory-scoped (`<dir>-<doc>-figN`) so
  same-named docs in different folders no longer collide on a shared basename
  (the initial `docs/deep-dive` batch keeps its bare `<doc>-figN` names).
