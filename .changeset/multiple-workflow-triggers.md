---
"agentweaver": patch
---

Allow one workflow to keep a recurring schedule and a GitHub event trigger at the same time, with
independent editing, API round-trips, and runtime dispatch for both. The event editor now also makes
GitHub Issues actions explicit, so label-driven workflows can select `labeled` instead of silently
remaining scoped to issue creation.
