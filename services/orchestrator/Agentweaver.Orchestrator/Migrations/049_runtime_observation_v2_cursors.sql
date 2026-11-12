CREATE TABLE {schema}.runtime_observation_v2_cursors (
    cursor_id uuid PRIMARY KEY,
    project_id text NOT NULL,
    run_id text NOT NULL,
    cursor_state jsonb NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX ix_runtime_observation_v2_cursors_expiry
    ON {schema}.runtime_observation_v2_cursors (expires_at);

CREATE INDEX ix_runtime_observation_v2_attempt
    ON {schema}.outbox_events (stream_id, (payload->>'attemptId'), sequence)
    WHERE event_type = 'orchestrator.runtime_observation.fact' AND event_version = 1;
