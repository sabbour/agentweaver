---
"agentweaver": patch
---

Return an existing healthy preview on retry when its ready outcome committed before an
API restart, without creating another Gateway route or duplicate ready events. Otherwise
reclaim an expired publication attempt while fencing stale publishing and process cleanup;
concurrent live attempts remain mutually exclusive.
