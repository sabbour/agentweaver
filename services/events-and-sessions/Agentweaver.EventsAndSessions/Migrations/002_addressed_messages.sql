CREATE TABLE {schema}.messaging_provider_bindings (
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
    PRIMARY KEY (project_id, run_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.session_run_streams (project_id, run_id)
);

CREATE TABLE {schema}.addressed_message_threads (
    project_id varchar(256) NOT NULL,
    thread_id uuid NOT NULL,
    last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, thread_id)
);

CREATE TABLE {schema}.addressed_messages (
    project_id varchar(256) NOT NULL,
    message_id uuid NOT NULL,
    sender_run_id varchar(256) NOT NULL,
    sender_session_id varchar(256) NOT NULL,
    recipient_run_id varchar(256) NOT NULL,
    recipient_session_id varchar(256) NOT NULL,
    sender_issuer varchar(1024) NOT NULL,
    sender_identity_hash varchar(64) NOT NULL CHECK (length(sender_identity_hash) = 64),
    thread_id uuid NOT NULL,
    reply_to_id uuid,
    idempotency_key varchar(128) NOT NULL,
    thread_sequence bigint NOT NULL CHECK (thread_sequence > 0),
    sender_fence bigint NOT NULL CHECK (sender_fence > 0),
    recipient_fence bigint NOT NULL CHECK (recipient_fence > 0),
    claim_fence bigint NOT NULL DEFAULT 0 CHECK (claim_fence >= 0),
    delivery_mode varchar(16) NOT NULL CHECK (delivery_mode IN ('Immediate', 'Enqueue')),
    purpose varchar(32) NOT NULL CHECK (purpose IN (
        'Progress', 'Handoff', 'NeedsInput', 'Error', 'Steering', 'Question', 'ApprovalRequest', 'Proposal')),
    kind varchar(16) NOT NULL CHECK (kind IN ('Text', 'Steering', 'Question', 'Approval', 'Proposal')),
    request_id varchar(128),
    reply_correlation_id varchar(128),
    user_quote text,
    coordinator_instructions text,
    payload jsonb NOT NULL,
    canonical_input jsonb NOT NULL,
    status varchar(16) NOT NULL CHECK (status IN (
        'Accepted', 'Claimed', 'Delivered', 'Acknowledged', 'Expired', 'Undeliverable')),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    presented_at timestamptz,
    acknowledged_at timestamptz,
    failure_reason varchar(32) CHECK (failure_reason IN (
        'StaleFence', 'TargetCancelled', 'TargetCompleted', 'RecipientUnavailable')),
    claim_owner varchar(128),
    claimed_until timestamptz,
    PRIMARY KEY (project_id, message_id),
    UNIQUE (project_id, sender_run_id, sender_session_id, sender_issuer, sender_identity_hash, idempotency_key),
    UNIQUE (project_id, thread_id, thread_sequence),
    FOREIGN KEY (project_id, sender_run_id, sender_session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id),
    FOREIGN KEY (project_id, recipient_run_id, recipient_session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id),
    FOREIGN KEY (project_id, thread_id)
        REFERENCES {schema}.addressed_message_threads (project_id, thread_id),
    FOREIGN KEY (project_id, reply_to_id)
        REFERENCES {schema}.addressed_messages (project_id, message_id),
    FOREIGN KEY (project_id, sender_run_id)
        REFERENCES {schema}.messaging_provider_bindings (project_id, run_id),
    CHECK ((claim_owner IS NULL) = (claimed_until IS NULL)),
    CHECK ((status IN ('Claimed', 'Delivered')) = (claim_owner IS NOT NULL)),
    CHECK ((status = 'Acknowledged') = (acknowledged_at IS NOT NULL)),
    CHECK ((status = 'Undeliverable') = (failure_reason IS NOT NULL))
);

CREATE INDEX ix_addressed_messages_recipient_order
    ON {schema}.addressed_messages
        (project_id, recipient_run_id, recipient_session_id, delivery_mode, created_at, thread_sequence)
    WHERE status IN ('Accepted', 'Claimed', 'Delivered');

CREATE INDEX ix_addressed_messages_expiry
    ON {schema}.addressed_messages (expires_at)
    WHERE status IN ('Accepted', 'Claimed', 'Delivered');
