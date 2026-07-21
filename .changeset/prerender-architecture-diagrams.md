---
"agentweaver": patch
---

Pre-rendered the AKS architecture diagrams (README.md's "Block diagram" and
architecture-aks.md's "Component diagram") to static SVG instead of relying
on live Mermaid rendering. GitHub's built-in Mermaid renderer was clipping
long subgraph and node labels on these diagrams (e.g. "AKS Cluster" ->
"AKS Cluste", "PostgreSQL" -> "PostgreSQ"), and switching from `block-beta`
to `flowchart TB` syntax did not fix it. Diagram sources now live under
`docs/diagrams/*.mmd` and are rendered to matching `.svg` files via
`npm run docs:render-diagrams` (using `@mermaid-js/mermaid-cli`); CI checks
the committed SVGs stay in sync with their `.mmd` source via
`npm run docs:check-diagrams`.
