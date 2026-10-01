---
"agentweaver": patch
---

Prevent concurrent database updates from permanently failing composed workflow planning. Recover eligible PostgreSQL planning failures under their original run identities, preserving completed branch outputs and resuming at the saved composed stage instead of starting a replacement execution.
