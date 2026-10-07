ALTER TABLE {schema}.accepted_runs
    DROP CONSTRAINT accepted_runs_execution_state_check,
    ADD CONSTRAINT accepted_runs_execution_state_check
        CHECK (execution_state IN ('idle', 'active', 'blocked', 'completed', 'failed', 'indeterminate')),
    ADD COLUMN execution_cause_code varchar(128),
    ADD COLUMN execution_reference varchar(512),
    ADD CONSTRAINT accepted_runs_execution_outcome_check
        CHECK (
            (execution_state IN ('failed', 'indeterminate') AND
                execution_cause_code IS NOT NULL AND execution_reference IS NOT NULL) OR
            (execution_state NOT IN ('failed', 'indeterminate') AND
                execution_cause_code IS NULL AND execution_reference IS NULL)
        );

ALTER TABLE {schema}.coordination_sessions
    DROP CONSTRAINT coordination_sessions_turn_state_check,
    ADD CONSTRAINT coordination_sessions_turn_state_check
        CHECK (turn_state IN ('idle', 'presenting', 'active', 'blocked', 'completed', 'failed', 'indeterminate'));

CREATE TABLE {schema}.coordination_execution_operations (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    operation_id uuid NOT NULL,
    session_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    operation_kind varchar(16) NOT NULL
        CHECK (operation_kind IN ('failure', 'recovery')),
    idempotency_key varchar(128) NOT NULL,
    request_hash char(64) NOT NULL CHECK (length(request_hash) = 64),
    result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, operation_id),
    UNIQUE (project_id, run_id, actor_issuer, actor_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
);
