CREATE TABLE {schema}.runtime_run_start_intents (
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    intent_id uuid NOT NULL UNIQUE,
    bootstrap_operation_id uuid NOT NULL UNIQUE,
    consume_operation_id uuid NOT NULL UNIQUE,
    exchange_operation_id uuid NOT NULL UNIQUE,
    state varchar(24) NOT NULL DEFAULT 'pending'
        CHECK (state IN ('pending', 'ready', 'blocked', 'cleanup_pending', 'cleanup_complete')),
    current_step varchar(48) NOT NULL DEFAULT 'accepted',
    failure_code varchar(96),
    environment_id varchar(256),
    runtime_instance_id uuid,
    receipt_links jsonb NOT NULL DEFAULT '{}'::jsonb
        CHECK (jsonb_typeof(receipt_links) = 'object'),
    cleanup_obligation boolean NOT NULL DEFAULT false,
    row_version bigint NOT NULL DEFAULT 1 CHECK (row_version > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash),
    FOREIGN KEY (project_id, run_id) REFERENCES {schema}.accepted_runs(project_id, run_id)
);
