---
"agentweaver": patch
---

Fix a production startup outage where Copilot App validation treated a correctly configured GitHub App with no extra permissions as having repository permissions, crashing the API on every boot.