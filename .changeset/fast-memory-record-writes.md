---
"agentweaver": patch
---

Prevent `record_memory` from timing out when project workspace storage is slow by returning
after the durable database write and leaving filesystem snapshot generation to the explicit
end-of-run memory export.
