---
"agentweaver": patch
---

Allow overriding the Postgres Flexible Server HA mode via `--postgres-ha-mode`/`PG_HA_MODE` to support regions and environments where zone-redundant HA is unavailable, including early-access/canary regions such as `eastus2euap`. Also fix Postgres server-name validation to reject names shorter than 3 characters.
