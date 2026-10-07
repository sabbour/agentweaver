CREATE TABLE {schema}.session_fork_lineage (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    target_session_id varchar(256) NOT NULL,
    source_session_id varchar(256) NOT NULL,
    source_event_id uuid NOT NULL,
    source_position bigint NOT NULL CHECK (source_position > 0),
    cursor_version integer NOT NULL CHECK (cursor_version > 0),
    source_schema_version integer NOT NULL CHECK (source_schema_version > 0),
    source_event_version integer NOT NULL CHECK (source_event_version > 0),
    provider_binding_hash char(64) NOT NULL CHECK (length(provider_binding_hash) = 64),
    source_cursor varchar(2048) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    command_hash char(64) NOT NULL CHECK (length(command_hash) = 64),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, target_session_id),
    UNIQUE (project_id, run_id, actor_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, target_session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, source_session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, source_event_id)
        REFERENCES {schema}.session_events (project_id, run_id, event_id),
    CHECK (target_session_id <> source_session_id)
);

CREATE INDEX ix_session_fork_lineage_source
    ON {schema}.session_fork_lineage (project_id, run_id, source_session_id, source_position);
