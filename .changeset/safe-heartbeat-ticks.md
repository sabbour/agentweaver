---
"agentweaver": patch
---

Keep the API running when a transient coordinator-heartbeat dependency fails, record a redacted failed tick, and retry on the next configured interval.
