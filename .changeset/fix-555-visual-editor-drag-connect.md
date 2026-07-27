---
"agentweaver": patch
---

Fix the Visual Workflow Editor so nodes can be connected by dragging (#555). The editor reuses the shared read-only `WorkflowNode` component, whose connection handles were hard-coded to `{ opacity: 0, pointerEvents: 'none' }` — correct for read-only graph renders (CoordinatorRunPage, WorkflowGraphPanel, LandingWorkflowDemo, all `nodesConnectable={false}`), but it meant React Flow never received the `pointerdown` needed to *start* a connection drag on the editable canvas, so the wired-up `onConnect` handler could never fire and edges could not be authored. Handle interactivity is now gated on a new `connectable` flag in `WorkflowNodeData`: read-only surfaces keep the invisible, non-interactive edge anchors, while `VisualWorkflowEditor` renders visible, `pointer-events: all`, `isConnectable` handles so drag-to-connect works.
