---
"agentweaver": patch
---

Fix independent task promotion so story components are classified concurrently with a
runtime-aligned timeout and one bounded retry. Classification degradation remains
fail-closed but is now surfaced on the work-plan timeline instead of silently producing
an unexplained empty board.
