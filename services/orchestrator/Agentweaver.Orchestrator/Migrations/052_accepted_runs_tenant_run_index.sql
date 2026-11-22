-- Indexes tenant-scoped run lookup across visible projects for ResolveVisibleRunProjectAsync.
-- Index creation blocks writes to accepted_runs while building; review table size before applying.
CREATE INDEX IF NOT EXISTS ix_accepted_runs_tenant_run_project
    ON {schema}.accepted_runs(tenant_id, run_id, project_id);
