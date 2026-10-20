-- Bounds project reporting reads without changing immutable ledger data.
CREATE INDEX ix_usage_ledger_project_occurred
    ON {schema}.usage_ledger (tenant_id, project_id, occurred_at);
