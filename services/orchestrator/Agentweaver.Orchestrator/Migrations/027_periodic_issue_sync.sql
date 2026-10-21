CREATE TABLE {schema}.issue_sync_projects (
    project_id text PRIMARY KEY REFERENCES {schema}.backlog_projects(project_id),
    tenant_id text NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    settings jsonb NOT NULL,
    grant_proof jsonb NOT NULL,
    next_cycle_at timestamptz NOT NULL,
    last_cycle_at timestamptz,
    status text NOT NULL CHECK (status IN ('configured', 'disabled', 'running', 'succeeded', 'failed')),
    error_code text,
    import_page integer NOT NULL DEFAULT 1 CHECK (import_page > 0),
    task_cursor text NOT NULL DEFAULT ''
);
CREATE INDEX ix_issue_sync_due ON {schema}.issue_sync_projects(next_cycle_at, project_id);

CREATE TABLE {schema}.issue_sync_receipts (
    project_id text NOT NULL REFERENCES {schema}.issue_sync_projects(project_id),
    task_id text NOT NULL,
    repository text NOT NULL,
    issue_number bigint CHECK (issue_number > 0),
    baseline jsonb,
    create_pending boolean NOT NULL DEFAULT false,
    PRIMARY KEY (project_id, task_id),
    UNIQUE (project_id, repository, issue_number),
    CHECK (baseline IS NULL OR issue_number IS NOT NULL)
);
