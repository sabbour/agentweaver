---
"agentweaver": patch
---

Keep preview publication alive throughout DNS and Gateway convergence. The API now renews a short
durable publication lease, coordinator stall detection honors that active work, and explicit run
cancellation still interrupts publication immediately.
