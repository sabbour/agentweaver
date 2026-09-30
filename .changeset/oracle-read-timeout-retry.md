---
"agentweaver": patch
---

Keep Oracle acceptance running through brief read timeouts by retrying temporary
GET failures within the existing attempt and phase limits. Review decisions and
other writes still run only once.
