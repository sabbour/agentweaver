CREATE TABLE {schema}.runtime_native_approval_suspensions (
    owner_turn_id varchar(256) NOT NULL,
    operation_id uuid NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    request_hash char(64) NOT NULL,
    policy_input_hash char(64) NOT NULL,
    retained_evaluation jsonb NOT NULL,
    approval_state_version bigint NULL,
    execution_fence bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    consumed_at timestamptz NULL,
    PRIMARY KEY (owner_turn_id, operation_id),
    CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    CHECK (policy_input_hash ~ '^[0-9a-f]{64}$'),
    CHECK (approval_state_version IS NULL OR approval_state_version >= 1),
    CHECK (execution_fence >= 1),
    CHECK (octet_length(retained_evaluation::text) <= 65536)
);

CREATE INDEX ix_runtime_native_approval_suspensions_owner
    ON {schema}.runtime_native_approval_suspensions
        (project_id, run_id, session_id, owner_turn_id, created_at);
