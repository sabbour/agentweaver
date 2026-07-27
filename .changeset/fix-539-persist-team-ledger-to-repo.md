---
"agentweaver": patch
---

Persist the team decision/memory ledger to the repository and report export failures honestly (#539). The DB-backed ledger (`.squad/decisions.md` and `.agentweaver/context/*`) is now mirrored into each run's git worktree at commit time, so it rides the same commit/push flow as the run's other changes and actually lands in the user's repo (previously it was only written to the base checkout, which is never committed). `POST /memory/export` (and the shared exporter) now return an actionable error instead of unconditionally reporting `{exported: true}` when the on-disk write fails.
