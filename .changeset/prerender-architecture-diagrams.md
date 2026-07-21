---
"agentweaver": patch
---

Pre-rendered the AKS architecture diagrams (README.md's "Block diagram" and
architecture-aks.md's "Component diagram", both simplified and detailed) to
static SVG instead of relying on live Mermaid rendering. GitHub's built-in
Mermaid renderer was clipping long subgraph and node labels on these
diagrams (e.g. "AKS Cluster" -> "AKS Cluste", "PostgreSQL" -> "PostgreSQ"),
and switching from `block-beta` to `flowchart TB` syntax did not fix it.

Diagram sources are now plain node/edge/group JSON definitions under
`docs/diagrams/specs/*.json`, auto-laid-out with `dagre` (no hand-placed
coordinates) and rendered through a small standalone `@xyflow/react` +
Playwright harness (`scripts/docs/render-diagrams/`) that mounts a real
React Flow graph, lets dagre position it, and exports a clean, self-contained
SVG (plain `<rect>`/`<text>`/`<path>`, no `foreignObject`) so it renders
identically on GitHub and in the VitePress docs site. Node colors and the
overall visual language carry over 1:1 from the original Mermaid `classDef`
categories (client/svc/core/worker/runtime/data/ext).

An earlier version of this change used `@mermaid-js/mermaid-cli` to
pre-render from `.mmd` sources, but its CI drift-check compared rendered SVG
geometry, which turned out to vary between Windows (dev machine) and Linux
(CI) font metrics -- the exact class of environment-sensitive-rendering bug
this change is meant to eliminate. `@mermaid-js/mermaid-cli` has been
dropped entirely in favor of the React Flow/dagre/Playwright pipeline, and
the drift-check (`npm run docs:check-diagrams`) is now a pure, dependency-free
SHA-256 hash comparison of each source JSON against `docs/diagrams/manifest.json`
-- it never re-renders or compares geometry, so it can't be OS-sensitive by
construction. Regenerate diagrams with `npm run docs:render-diagrams` after
editing a `docs/diagrams/specs/*.json` source; CI fails if the committed SVG's
source hash drifts from the manifest.
