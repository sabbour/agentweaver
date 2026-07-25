---
'agentweaver': patch
---

Fail fast when the UI harness reuses an empty Playwright storage state so staging dry-runs report AUTH_EXPIRED instead of proceeding with a broken session.
