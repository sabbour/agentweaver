---
"agentweaver": patch
---

fix: persist terminal WorkPlan status when coordinator run already stopped

CoordinatorDispatchService detected a terminal coordinator run but returned early without calling SetWorkPlanStatusAsync, leaving WorkPlans permanently stuck in dispatching. This caused the reconciler to re-arm every ~10 s forever (infinite loop). Fix calls SetWorkPlanStatusAsync before the early return and adds a regression test.

Fixes #808.
