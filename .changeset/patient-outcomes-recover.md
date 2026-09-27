---
"agentweaver": patch
---

Keep outcome-spec confirmation and revision available when the coordinator moves between API replicas or restarts. Recover the persisted gate under its run lease, report unsafe recovery with an attributable error, and distinguish decisions queued for another replica from completed work.
