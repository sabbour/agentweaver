-- Records each pull request that an accepted run created or reused through the pinned Source Control provider.
-- The project pull-request list reads these rows only; it never calls the provider at read time.
-- Coverage starts when this migration is applied: earlier pull requests, and pull requests that no run
-- created or reused, have no row.
CREATE TABLE IF NOT EXISTS {schema}.source_control_pull_requests (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    pin_id varchar(128) NOT NULL,
    provider_id varchar(128) NOT NULL,
    repository_owner varchar(100) NOT NULL,
    repository_name varchar(100) NOT NULL,
    pull_request_number bigint NOT NULL CHECK (pull_request_number > 0),
    html_url varchar(2048) NOT NULL,
    title text,
    state varchar(16) NOT NULL CHECK (state IN ('open', 'closed', 'merged')),
    head_branch varchar(1024) NOT NULL,
    head_sha varchar(64) NOT NULL,
    base_branch varchar(1024) NOT NULL,
    base_sha varchar(64) NOT NULL,
    disposition varchar(16) NOT NULL CHECK (disposition IN ('created', 'reused')),
    recorded_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, run_id, pin_id, pull_request_number),
    FOREIGN KEY (project_id, run_id, pin_id)
        REFERENCES {schema}.source_control_repository_pins(project_id, run_id, pin_id),
    CHECK (updated_at >= recorded_at)
);

CREATE INDEX IF NOT EXISTS ix_source_control_pull_requests_project_recorded
    ON {schema}.source_control_pull_requests
        (project_id, recorded_at DESC, run_id DESC, pin_id DESC, pull_request_number DESC);