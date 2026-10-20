-- Durable interactive requests retain their identity across browser rebinding and retries.
CREATE TABLE {schema}.project_run_requests (
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    title varchar(256) NOT NULL,
    first_message text NOT NULL CHECK (octet_length(first_message) BETWEEN 1 AND 32768),
    workflow_id varchar(256),
    expected_project_config_revision bigint NOT NULL CHECK (expected_project_config_revision > 0),
    started_by_issuer varchar(512) NOT NULL,
    started_by_subject varchar(256) NOT NULL,
    idempotency_key_hash char(64) NOT NULL,
    request_fingerprint char(64) NOT NULL,
    state varchar(32) NOT NULL DEFAULT 'awaiting_binding'
        CHECK (state IN ('awaiting_binding', 'started', 'cancelled')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id),
    UNIQUE (tenant_id, project_id, started_by_issuer, started_by_subject, idempotency_key_hash)
);

ALTER TABLE {schema}.accepted_runs
    ADD COLUMN cancelled_at timestamptz,
    DROP CONSTRAINT accepted_runs_execution_state_check,
    ADD CONSTRAINT accepted_runs_execution_state_check
        CHECK (execution_state IN ('idle', 'active', 'blocked', 'completed', 'failed', 'indeterminate', 'cancelled'));

-- Cancellation receipts also cover runs accepted through non-interactive clients.
CREATE TABLE {schema}.project_run_cancellations (
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    idempotency_key_hash char(64) NOT NULL,
    request_fingerprint char(64) NOT NULL,
    result jsonb NOT NULL,
    PRIMARY KEY (tenant_id, project_id, run_id, actor_issuer, actor_subject, idempotency_key_hash)
);

-- Keyset order for the project run list: most recently updated first, run_id as the tie breaker.
CREATE INDEX ix_accepted_runs_project_updated
    ON {schema}.accepted_runs(tenant_id, project_id, updated_at DESC, run_id);
