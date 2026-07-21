---
"agentweaver": patch
---

Fixed Web lint and Web test CI breaks introduced after the Changesets
integration landed: extracted non-component exports out of
`CostChip.tsx`/`BlueprintPicker.tsx`/`LandingWorkflowDemo.tsx` into sibling
modules to satisfy `react-refresh/only-export-components`, removed a dead
reassignment flagged by `no-useless-assignment`, and fixed a real
`CoordinatorRunPage` test flake caused by a missing global `afterEach`
cleanup between test files (added `apps/web/src/test/setup.ts` and made
dialog-button role queries more resilient to CPU-contention timing).
