---
"agentweaver": patch
---

Reorder Kata runtime gate before the full .NET test suite so failures surface in ~90s instead of ~8min. Add max 2 retries on the gate step only. Fix 4 flaky Kata/sandbox timing tests.
