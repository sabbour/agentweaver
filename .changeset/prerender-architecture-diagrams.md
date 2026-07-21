---
"agentweaver": patch
---

Re-rendered the AKS architecture diagrams (README.md's "Block diagram" and
architecture-aks.md's "Component diagram", simplified + detailed) as
Fluent-styled node/card diagrams instead of generic flowchart output. GitHub's
built-in Mermaid renderer was clipping long subgraph/node labels on these
diagrams, and a static pre-render was the fix -- but the pre-rendered result
(first via `@mermaid-js/mermaid-cli`, then via a plain React Flow SVG export)
still looked nothing like the product's own polished, on-brand node/edge
diagrams (`apps/web/src/components/CoordinatorTopologyGraph.tsx`).

Diagrams are now driven by plain JSON graph-specs (`docs/diagrams/src/*.json`)
rendered through a small standalone app (`docs/diagram-renderer/`) that mounts
a real `@xyflow/react` graph with `dagre` compound-cluster auto-layout and a
custom node-card component matching `CoordinatorTopologyGraph`'s Fluent UI v9
card styling (rounded card, icon + title + subtitle, pill category badges,
tiered group containers) using the app's actual resolved color palette.
Playwright captures each diagram as a static PNG
(`scripts/docs/capture-diagrams.mjs`). `npm run docs:render-diagrams`
regenerates the PNGs from the JSON specs; `npm run docs:check-diagrams` (CI)
is now a fast, browser-free drift check comparing each spec's content hash
against a committed `.hash.txt`, rather than re-rendering and diffing
geometry (which broke across OSes due to host font-metric differences).
