---
"agentweaver": patch
---

Prevent coordinator learnings from being lost when a terminal run recycles its AgentHost pod
before the final Scribe turn. Terminal cleanup now waits for the bounded Scribe pass to finish,
then releases the per-run pod and assembly worktree.
