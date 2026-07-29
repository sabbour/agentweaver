---
"agentweaver": patch
---

Fix terminal merge conflicts on Squad bookkeeping files across concurrent coordinator runs (#621). A new project-level `SquadStateConsolidationService` is now the sole writer of the canonical decision ledger — it idempotently drains `.squad/decisions/inbox/*.md` into `.squad/decisions.md` on the project's default branch, decoupled from any run's branch-merge lifecycle. Per-run branch merges now resolve the canonical Squad ledgers (`.squad/decisions.md`, `.squad/agents/*/history.md`, `.squad/identity/now.md`) path-level "ours", so a run's racing copy can no longer produce a human-resolution-required conflict or clobber consolidated content, while genuine conflicts on every other path are still detected.
