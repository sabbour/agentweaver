CREATE TABLE "{schema}".outbox_streams (
    stream_id text PRIMARY KEY,
    last_sequence bigint NOT NULL DEFAULT 0
);

CREATE TABLE "{schema}".outbox_events (
    id uuid PRIMARY KEY,
    stream_id text NOT NULL REFERENCES "{schema}".outbox_streams (stream_id),
    sequence bigint NOT NULL,
    idempotency_key text NOT NULL UNIQUE,
    event_type text NOT NULL,
    event_version integer NOT NULL,
    payload jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    leased_until timestamptz,
    lease_token uuid,
    worker_id text,
    delivered_at timestamptz,
    UNIQUE (stream_id, sequence)
);

CREATE INDEX outbox_events_pending_idx ON "{schema}".outbox_events (stream_id, sequence)
    WHERE delivered_at IS NULL;
