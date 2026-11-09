CREATE TABLE {schema}.runtime_replacement_operations (
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    operation_id uuid NOT NULL UNIQUE,
    request_hash char(64) NOT NULL,
    expected_runtime_instance_id uuid NOT NULL,
    expected_registration_revision bigint NOT NULL CHECK (expected_registration_revision > 0),
    accepted_selection_hash char(64) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    state varchar(24) NOT NULL DEFAULT 'pending'
        CHECK (state IN ('pending', 'ready', 'blocked', 'failed', 'cleanup_pending', 'cleanup_complete')),
    current_step varchar(48) NOT NULL DEFAULT 'accepted',
    failure_code varchar(96),
    receipt_links jsonb NOT NULL DEFAULT '{}'::jsonb
        CHECK (jsonb_typeof(receipt_links) = 'object'),
    row_version bigint NOT NULL DEFAULT 1 CHECK (row_version > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, project_id, run_id, root_session_id, operation_id),
    FOREIGN KEY (project_id, run_id) REFERENCES {schema}.accepted_runs(project_id, run_id)
);

CREATE UNIQUE INDEX uq_runtime_replacement_one_active
    ON {schema}.runtime_replacement_operations
        (tenant_id, project_id, run_id, root_session_id)
    WHERE state = 'pending';
