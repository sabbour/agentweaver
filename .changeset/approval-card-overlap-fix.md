---
'agentweaver': patch
---

Stop the in-thread "Tool Approval Required" card from overlapping the agent activity feed on run/orchestration detail views. The run timeline could collapse to zero height under flex pressure inside its scroll container, letting its accordion content overflow visibly and the following approval card render on top of it; the timeline root now reserves its full content height (`flex-shrink: 0`) so sibling content always flows below it.
