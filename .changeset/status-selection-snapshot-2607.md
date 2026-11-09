---
"Agentweaver.Orchestrator": patch
---

Build the session status runtime startup projection from the accepted selection
that the status read checks again before it returns. A changed selection now
returns `409 coordinator_selection_stale` again.
