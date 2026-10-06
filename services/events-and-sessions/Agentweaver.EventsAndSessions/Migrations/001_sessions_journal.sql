CREATE TABLE IF NOT EXISTS {schema}.sessions_schema_migrations (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS {schema}.session_provider_bindings (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    provider_id varchar(256) NOT NULL,
    adapter_version varchar(64) NOT NULL,
    options_schema_version integer NOT NULL CHECK (options_schema_version > 0),
    options_revision varchar(128) NOT NULL,
    resource_id varchar(256) NOT NULL,
    resource_generation bigint NOT NULL CHECK (resource_generation > 0),
    negotiated_capabilities jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id)
);

CREATE TABLE IF NOT EXISTS {schema}.session_run_streams (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    last_position bigint NOT NULL DEFAULT 0 CHECK (last_position >= 0),
    PRIMARY KEY (project_id, run_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.session_provider_bindings (project_id, run_id)
);

CREATE TABLE IF NOT EXISTS {schema}.sessions (
    session_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.session_run_streams (project_id, run_id)
);

CREATE TABLE IF NOT EXISTS {schema}.session_events (
    event_id uuid NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    position bigint NOT NULL CHECK (position > 0),
    schema_version integer NOT NULL CHECK (schema_version > 0),
    event_version integer NOT NULL CHECK (event_version > 0),
    event_kind varchar(64) NOT NULL,
    occurred_at timestamptz NOT NULL,
    payload jsonb NOT NULL,
    canonical_input jsonb NOT NULL,
    object_references jsonb NOT NULL,
    PRIMARY KEY (project_id, run_id, event_id),
    UNIQUE (project_id, run_id, position),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id)
);

CREATE INDEX IF NOT EXISTS ix_session_events_replay
    ON {schema}.session_events (project_id, run_id, session_id, position);

CREATE TABLE IF NOT EXISTS {schema}.session_object_references (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    event_id uuid NOT NULL,
    object_key varchar(1024) NOT NULL,
    purpose varchar(64) NOT NULL,
    byte_length bigint CHECK (byte_length IS NULL OR byte_length >= 0),
    retention_owner_id varchar(64) NOT NULL,
    retain_until timestamptz NOT NULL,
    PRIMARY KEY (project_id, run_id, event_id, object_key, purpose),
    FOREIGN KEY (project_id, run_id, event_id)
        REFERENCES {schema}.session_events (project_id, run_id, event_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_session_object_retention
    ON {schema}.session_object_references (retain_until);
