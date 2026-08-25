---
"agentweaver": patch
---

Fix the Trace Summary "Latest" stat to compute MAX(started_at) across all candidates instead
of trusting list order, reverse "Recent coordinator runs" back to newest-first (matching the
`/runs` API's deterministic newest-first order), and add a "View trace" button on the
orchestration run detail page that jumps directly to that run's trace.
