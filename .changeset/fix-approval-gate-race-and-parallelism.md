---
"agentweaver": patch
---

Bound xUnit test collection parallelism to two workers and made tool-approval
gate terminal-state resolution atomic (guarding replacement cleanup so a
gate can't be resolved twice under concurrent access). Also gave
approval-expiration tests more scheduler headroom to reduce CI flakiness.
