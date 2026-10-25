CREATE TABLE {schema}.source_control_pull_request_operations (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    operation_id uuid NOT NULL,
    pin_id varchar(128) NOT NULL,
    request_fingerprint char(64) NOT NULL,
    pull_request_number bigint,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz,
    PRIMARY KEY (project_id, run_id, session_id, operation_id),
    CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    CHECK (request_fingerprint ~ '^[0-9a-f]{64}$'),
    CHECK (pull_request_number IS NULL OR pull_request_number > 0),
    CHECK ((pull_request_number IS NULL) = (completed_at IS NULL))
);
