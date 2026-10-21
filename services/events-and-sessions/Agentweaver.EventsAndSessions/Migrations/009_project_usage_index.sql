-- Bounds reporting reads and tolerates an existing same-named index.
-- The index build blocks writes to usage_ledger while it runs.
CREATE INDEX IF NOT EXISTS ix_usage_ledger_project_occurred
    ON {schema}.usage_ledger (tenant_id, project_id, occurred_at);
