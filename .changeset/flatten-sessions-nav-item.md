---
"agentweaver": patch
---

Flatten the "Sessions" nav item to a single `[Sessions] [+]` row instead of an expandable
disclosure. The disclosure's only child link ("All sessions") pointed at the exact same route
as the parent item, so expanding it added a redundant click with no new information. Sessions
now behaves like the other global nav items — a direct link plus the "New session" action
button on the same row.
