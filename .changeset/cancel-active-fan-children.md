---
"agentweaver": patch
---

Stop active static fan branch runs for top-level and nested plans when their parent is cancelled, while preserving pending branches and suppressing the parent join across retries and restarts.
