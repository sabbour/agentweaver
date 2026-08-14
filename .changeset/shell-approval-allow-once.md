---
"agentweaver": patch
---

fix(web): shell "Allow once" approval now works from coordinator view

Root causes and fixes:
- **isShell detection**: `coordinator.child_approval_required` events with a `commandHash` field are shell approvals bubbled from child runs. Detection was extended so `InThreadApprovalGate` correctly identifies them as shell approvals and calls `approveShell`/`denyShell` instead of `approveTool`/`denyTool`.
- **Wrong run ID**: Shell approval API calls now target the child run (`childRunId` from event payload) instead of the coordinator run, preventing 404s from the backend.
- **Resolution tracking**: `buildCoordinatorTurns` now uses `commandHash` as a fallback key when `requestId` is absent, so resolved shell approvals display correctly.
- **Disabled state**: `ApprovalGate` now accepts a `disabled` prop that is passed through to both "Allow once" and "Deny" buttons; the gate disables while a request is in flight.
- **UX**: Added a "Review" button in the "Needs input" MessageBar that scrolls to the pending approval gate, reducing the chance of it being missed.
