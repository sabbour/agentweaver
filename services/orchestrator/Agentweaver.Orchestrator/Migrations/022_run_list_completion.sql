-- Records when a run reached a terminal outcome and indexes project-scoped run reads.
-- completed_at is set going forward only; runs that ended before this migration keep NULL.
ALTER TABLE {schema}.accepted_runs
    ADD COLUMN completed_at timestamptz,
    ADD CONSTRAINT accepted_runs_completed_at_check
        CHECK (completed_at IS NULL OR completed_at >= created_at);

-- Keyset order for the project run list: newest first, run_id as the tie breaker.
CREATE INDEX ix_accepted_runs_project_created
    ON {schema}.accepted_runs(tenant_id, project_id, created_at DESC, run_id DESC);

-- Range scans for project metrics over terminal runs.
CREATE INDEX ix_accepted_runs_project_completed
    ON {schema}.accepted_runs(tenant_id, project_id, completed_at)
    WHERE completed_at IS NOT NULL;
