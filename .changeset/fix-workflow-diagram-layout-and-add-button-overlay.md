---
"agentweaver": patch
---

Fix the workflow visual diagram (WorkflowsPage "View graph" panel and the visual workflow editor)
so both use the same staircase auto-layout mechanism as the coordinator run topology view, and fix
a broken "Add next step" button that visually overlapped node content in the editor.

- `VisualWorkflowEditor.tsx` was still laying its canvas out with the plain `layoutDag` (a single
  straight LR dagre row) and single left/right node handles, unlike the read-only
  `WorkflowDefinitionInlinePanel` (WorkflowsPage) and `CoordinatorRunPage`, which both use
  `layoutDagStaircase` + `routeGridEdges` + GRID (all-side) node handles for a compact, legible,
  non-overlapping diagram. The editor now builds its graph the same way, so both surfaces read
  identically instead of the editor's canvas looking sparse/unstructured by comparison.
- In `WorkflowGraphPanel.tsx`'s shared `WorkflowNode`, the editor's "Add next step" button + actions
  menu was rendered as a sibling of the node's icon/title row inside the same single-row flex
  container, so it got squeezed onto — and visually overlapped — the node's title/sub-label instead
  of appearing below it. It now renders as the last row inside the node body (matching how the
  Human Review gate's on-face action already stacks below its content), so it sits cleanly under the
  node text with no overlap.
