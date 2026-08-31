---
"agentweaver": patch
---

Harden GitHub Copilot platform-connection handling so malformed saved configuration does not block recovery, platform/default and project-scoped bindings safely clean up or preserve shared credentials, and SQLite-to-Postgres migration carries the platform-default binding forward reliably.