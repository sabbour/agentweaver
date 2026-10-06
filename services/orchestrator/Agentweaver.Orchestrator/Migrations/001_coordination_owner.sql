CREATE TABLE IF NOT EXISTS {schema}.coordination_schema_migrations (
    version integer PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS {schema}.accepted_runs (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    accepted_selection jsonb NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    accepted_by_issuer varchar(512) NOT NULL,
    accepted_by_subject varchar(256) NOT NULL,
    execution_fence bigint NOT NULL DEFAULT 1 CHECK (execution_fence > 0),
    logical_turn_ordinal bigint NOT NULL DEFAULT 0 CHECK (logical_turn_ordinal >= 0),
    execution_state varchar(16) NOT NULL DEFAULT 'idle'
        CHECK (execution_state IN ('idle', 'active', 'blocked', 'completed')),
    pending_wake boolean NOT NULL DEFAULT false,
    state_version bigint NOT NULL DEFAULT 1 CHECK (state_version > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id)
);

CREATE TABLE IF NOT EXISTS {schema}.coordination_sessions (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    parent_session_id varchar(256),
    writer_issuer varchar(512) NOT NULL,
    writer_subject varchar(256) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    lifecycle_state varchar(16) NOT NULL DEFAULT 'active'
        CHECK (lifecycle_state IN ('active', 'cancelled', 'completed')),
    logical_turn_ordinal bigint NOT NULL DEFAULT 0 CHECK (logical_turn_ordinal >= 0),
    turn_state varchar(16) NOT NULL DEFAULT 'idle'
        CHECK (turn_state IN ('idle', 'presenting', 'active', 'blocked', 'completed')),
    turn_boundary_request_version bigint CHECK (turn_boundary_request_version > 0),
    turn_boundary_result jsonb,
    state_version bigint NOT NULL DEFAULT 1 CHECK (state_version > 0),
    pending_wake boolean NOT NULL DEFAULT false,
    history_visible_to_parent boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id) REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, parent_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
        DEFERRABLE INITIALLY DEFERRED,
    CHECK (turn_boundary_result IS NULL OR turn_boundary_request_version IS NOT NULL),
    CHECK (parent_session_id IS NULL OR parent_session_id <> session_id)
);

CREATE TABLE IF NOT EXISTS {schema}.coordination_messages (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    message_id uuid NOT NULL,
    sender_session_id varchar(256) NOT NULL,
    recipient_session_id varchar(256) NOT NULL,
    writer_issuer varchar(512) NOT NULL,
    writer_subject varchar(256) NOT NULL,
    thread_id uuid NOT NULL,
    sender_fence bigint NOT NULL CHECK (sender_fence > 0),
    recipient_fence bigint NOT NULL CHECK (recipient_fence > 0),
    idempotency_key varchar(128) NOT NULL,
    purpose varchar(32) NOT NULL,
    kind varchar(32) NOT NULL,
    delivery_mode varchar(16) NOT NULL CHECK (delivery_mode IN ('Immediate', 'Enqueue')),
    request_id varchar(128),
    reply_to_id uuid,
    reply_correlation_id varchar(128),
    canonical_request jsonb NOT NULL,
    payload jsonb NOT NULL,
    user_quote text,
    coordinator_instructions text,
    status varchar(24) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'admitted', 'presented', 'acknowledged', 'cancelled')),
    events_message_id uuid,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, message_id),
    UNIQUE (project_id, run_id, sender_session_id, writer_issuer, writer_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, sender_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, recipient_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, reply_to_id)
        REFERENCES {schema}.coordination_messages(project_id, message_id)
        DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE IF NOT EXISTS {schema}.coordination_requests (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    request_id varchar(128) NOT NULL,
    source_message_id uuid,
    sender_session_id varchar(256) NOT NULL,
    recipient_session_id varchar(256) NOT NULL,
    gate_state varchar(24) NOT NULL DEFAULT 'pending'
        CHECK (gate_state IN ('pending', 'input_available', 'cancelled')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, request_id),
    FOREIGN KEY (project_id, source_message_id)
        REFERENCES {schema}.coordination_messages(project_id, message_id)
);

CREATE TABLE IF NOT EXISTS {schema}.parent_notifications (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    notification_id uuid NOT NULL,
    parent_session_id varchar(256) NOT NULL,
    child_session_id varchar(256) NOT NULL,
    message_id uuid NOT NULL,
    notification_kind varchar(24) NOT NULL
        CHECK (notification_kind IN ('handoff', 'needs_input', 'error')),
    wakes_parent boolean NOT NULL,
    acknowledged_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, notification_id),
    UNIQUE (project_id, message_id),
    FOREIGN KEY (project_id, run_id, parent_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, child_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, message_id)
        REFERENCES {schema}.coordination_messages(project_id, message_id)
);

CREATE INDEX IF NOT EXISTS ix_coordination_sessions_writer
    ON {schema}.coordination_sessions(project_id, run_id, writer_issuer, writer_subject, lifecycle_state);
CREATE UNIQUE INDEX IF NOT EXISTS ux_coordination_root_per_run
    ON {schema}.coordination_sessions(project_id, run_id)
    WHERE parent_session_id IS NULL;
CREATE INDEX IF NOT EXISTS ix_coordination_notifications_parent
    ON {schema}.parent_notifications(project_id, run_id, parent_session_id, created_at)
    WHERE acknowledged_at IS NULL;
