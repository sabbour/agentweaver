---
"agentweaver": patch
---

Delete each generated demo-recording beat script (`scripts/demo-recording/.auth/generated/**/beat-*.cjs`) right after it runs instead of leaving it on disk. These single-use scripts embed seeded session data and previously accumulated indefinitely across recording runs, wasting local disk space.
