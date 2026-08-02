---
"agentweaver": patch
---

Allow overriding the Postgres Flexible Server name via `--postgres-server-name`/`PG_SERVER_NAME` to route around the rare case where the default `agentweaver-pg` name is already reserved elsewhere in Azure's global namespace.
