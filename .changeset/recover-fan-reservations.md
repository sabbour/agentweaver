---
"agentweaver": patch
---

Recover reserved pending fan-out branches under their original child IDs when the API coordinator changes pods, without waiting for an obsolete dispatch lease or mistaking an unstarted branch for a stalled agent.
