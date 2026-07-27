---
"agentweaver": patch
---

Fix the Workflow visual editor silently placing newly-added nodes outside the visible canvas viewport (#540). `<ReactFlow fitView>` only auto-fits on initial mount, so a node added via "Add node" (or a special gate) that the DAG layout positions outside the current viewport rendered behind the canvas pane's `overflow: hidden`, making the click look like it did nothing even though the node was added correctly. `VisualWorkflowEditor` now imperatively re-fits the viewport (via `useReactFlow().fitView`) whenever the node count grows, while leaving pan/zoom untouched for unrelated edits like renaming a node.
