---
"Agentweaver.Persistence.Postgres": patch
"Agentweaver.Environment": patch
---

Revoke runtime `INSERT` grants on the three Environment Policy ledger tables (`platform_policy_apply_reservations`, `platform_policy_proof_holds`, and `platform_policy_proof_receipts`), enforce fail-closed effective-privilege checks in `PlannedRuntimeGrants` and `EnvironmentMigrator`, and guard the schema-18 grant cutover in `db-maintenance`.
