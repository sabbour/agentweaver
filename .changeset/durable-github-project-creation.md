---
"agentweaver": patch
---

Reserve GitHub-origin projects before repository cloning so request cancellation cannot discard all project state, expose explicit creating and failed states, and make retries with the same repository selection code idempotent.
