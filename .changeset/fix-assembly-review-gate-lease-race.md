---
'agentweaver': patch
---

Fix a cross-pod race that could cause assembly to fail with
`agenthost_configure_failed` right as a run entered human review. The
work plan's status was flipped to `InReview` before the durable
`AssemblyReviews` row backing that gate was persisted, leaving a short
window where a peer pod's reconciler sweep could observe `InReview` with
no pending review row, conclude the run was orphaned, and re-arm
assembly — colliding with the still-live owner on the same AgentHost
claim mid-`/configure`. The review row is now persisted before the
status flip, closing the window.
