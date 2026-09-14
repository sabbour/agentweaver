---
"agentweaver": patch
---

Make persona similarity ranking completion-aware: `find-similar.mjs` now surfaces completion metadata, accepts `--requires-completion`, rejects gate-stopping personas for completion-required scenarios, and warns when the top keyword match stops before execution.
