---
"agentweaver": patch
---

fix(ux): auto-switch from outcome-plan to approval session on real-time approval event

When a coordinator.child_approval_required event arrives via SSE while the
outcome-plan panel is visible, automatically switch to the child agent's session
panel so the session-approval-gate is immediately visible without a manual tree
click. Previously users (and recordings) had to click a tree item to expose the
approval gate, which was non-obvious and caused beat 2.5 recording failures.
