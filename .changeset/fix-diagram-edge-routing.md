---
"agentweaver": patch
---

Fixed edge routing in the architecture-diagram renderer so lines no longer
overlap each other or cut straight through unrelated node cards. The renderer
was drawing every edge as a `getSmoothStepPath` between fixed top/bottom
handles, which ignores dagre's own edge routing -- so an edge from one rank to
a distant rank sliced right through any card in between, and several
near-parallel edges collapsed onto the same path.

The shared renderer (`docs/diagram-renderer/`) now draws each edge along the
poly-line dagre actually routes for it (`dagre` performs real layered edge
routing, threading each line through the gaps between ranked nodes), rendered
with rounded corners so it still reads like the product's smoothstep edges.
Labelled edges hand dagre their footprint up front so it reserves
non-overlapping label slots along the route, and the existing label
collision-avoidance pass now seeds from those reserved anchors. `nodesep`,
`edgesep`, and `ranksep` were widened for extra breathing room. This is a
general fix in the pipeline -- every current and future graph-spec benefits,
with no per-diagram tuning. The three AKS diagrams were re-rendered.
