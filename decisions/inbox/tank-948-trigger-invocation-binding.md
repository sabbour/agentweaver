### 2026-08-28: Bind trigger invocations by server-owned task identity
**By:** Tank
**What:** Schedule and event triggers first claim a project-fenced activation and then bind the created task ID to that durable invocation. Pickup resolves the invocation only through that binding and the expected project.
**Why:** Client-controlled backlog external IDs cannot select an invocation or inject unattended capability snapshots across project boundaries.
