ALTER TABLE {schema}.accepted_runs
    ADD COLUMN IF NOT EXISTS tenant_id varchar(256);

CREATE TABLE IF NOT EXISTS {schema}.coordinator_decisions (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    request_id varchar(128) NOT NULL,
    decision_id uuid NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    state_version bigint NOT NULL CHECK (state_version > 0),
    action_kind varchar(64) NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    command_hash char(64) NOT NULL,
    decision_state varchar(16) NOT NULL CHECK (decision_state IN ('accepted', 'rejected')),
    decision jsonb NOT NULL CHECK (jsonb_typeof(decision) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, decision_id),
    UNIQUE (project_id, run_id, session_id, actor_issuer, actor_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
);

CREATE TABLE IF NOT EXISTS {schema}.coordinator_decision_outbox (
    event_id uuid NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    decision_id uuid NOT NULL,
    event_kind varchar(64) NOT NULL,
    event jsonb NOT NULL CHECK (jsonb_typeof(event) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (event_id),
    UNIQUE (decision_id, event_kind),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, decision_id)
        REFERENCES {schema}.coordinator_decisions(project_id, run_id, decision_id)
);

CREATE TABLE IF NOT EXISTS {schema}.coordinator_gates (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    request_id varchar(128) NOT NULL,
    gate_kind varchar(32) NOT NULL CHECK (gate_kind IN (
        'outcome', 'workflow_first_use', 'work_plan', 'scope_change', 'question', 'approval')),
    gate_state varchar(16) NOT NULL CHECK (gate_state IN ('pending', 'approved', 'rejected', 'answered', 'cancelled')),
    gate jsonb NOT NULL CHECK (jsonb_typeof(gate) = 'object'),
    allowed_choices jsonb NOT NULL CHECK (jsonb_typeof(allowed_choices) = 'array'),
    allow_free_form boolean NOT NULL DEFAULT false,
    created_by_issuer varchar(512) NOT NULL,
    created_by_subject varchar(256) NOT NULL,
    resolved_by_issuer varchar(512),
    resolved_by_subject varchar(256),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    state_version bigint NOT NULL CHECK (state_version > 0),
    response jsonb CHECK (response IS NULL OR jsonb_typeof(response) = 'object'),
    idempotency_key varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, request_id),
    UNIQUE (project_id, run_id, session_id, created_by_issuer, created_by_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    CHECK ((gate_state IN ('pending', 'cancelled') AND response IS NULL)
        OR (gate_state IN ('approved', 'rejected', 'answered') AND response IS NOT NULL)),
    CHECK ((resolved_by_issuer IS NULL) = (resolved_by_subject IS NULL))
);

CREATE TABLE IF NOT EXISTS {schema}.executable_action_grants (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    grant_id varchar(128) NOT NULL,
    revision varchar(128) NOT NULL,
    is_current boolean NOT NULL DEFAULT true,
    grant_state varchar(16) NOT NULL CHECK (grant_state IN ('active', 'revoked', 'superseded')),
    issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    step_id varchar(128) NOT NULL,
    action_ids jsonb NOT NULL CHECK (jsonb_typeof(action_ids) = 'array'),
    purpose varchar(128) NOT NULL,
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    platform_runtime_revision bigint NOT NULL CHECK (platform_runtime_revision > 0),
    context_revision varchar(128) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    membership_revision bigint NOT NULL CHECK (membership_revision > 0),
    role_revision bigint NOT NULL CHECK (role_revision > 0),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    expires_at timestamptz NOT NULL,
    source_state_version bigint NOT NULL CHECK (source_state_version > 0),
    source_decision_id uuid NOT NULL,
    source_request_id varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, grant_id, revision),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, source_decision_id)
        REFERENCES {schema}.coordinator_decisions(project_id, run_id, decision_id)
);

CREATE TABLE IF NOT EXISTS {schema}.policy_evaluation_receipts (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    receipt_id uuid NOT NULL,
    issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    step_id varchar(128) NOT NULL,
    grant_id varchar(128) NOT NULL,
    grant_revision varchar(128) NOT NULL,
    purpose varchar(128) NOT NULL,
    action_id varchar(128) NOT NULL,
    outcome varchar(16) NOT NULL CHECK (outcome IN ('allow', 'deny', 'error')),
    reason_code varchar(64) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    provider_id varchar(128) NOT NULL,
    adapter_version varchar(64) NOT NULL,
    options_schema_version integer NOT NULL CHECK (options_schema_version > 0),
    options_revision varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, receipt_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, grant_id, grant_revision)
        REFERENCES {schema}.executable_action_grants(project_id, run_id, grant_id, revision)
);

CREATE TABLE IF NOT EXISTS {schema}.maf_workflow_checkpoints (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    store_name varchar(128) NOT NULL,
    checkpoint_id varchar(64) NOT NULL,
    parent_checkpoint_id varchar(64),
    has_parent_metadata boolean NOT NULL DEFAULT true,
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    cache_object_key varchar(1024),
    cache_sdk_version varchar(128) NOT NULL,
    pinned_model_reference varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, session_id, store_name, checkpoint_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    CHECK (parent_checkpoint_id IS NULL OR parent_checkpoint_id <> checkpoint_id),
    CHECK (cache_object_key IS NULL OR length(cache_object_key) > 0)
);

CREATE INDEX IF NOT EXISTS ix_coordinator_decisions_session
    ON {schema}.coordinator_decisions(project_id, run_id, session_id, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_coordinator_decisions_session_version
    ON {schema}.coordinator_decisions(project_id, run_id, session_id, state_version);
CREATE INDEX IF NOT EXISTS ix_coordinator_decision_outbox_session
    ON {schema}.coordinator_decision_outbox(project_id, run_id, session_id, created_at);
CREATE INDEX IF NOT EXISTS ix_coordinator_gates_pending
    ON {schema}.coordinator_gates(project_id, run_id, session_id, created_at)
    WHERE gate_state = 'pending';
CREATE INDEX IF NOT EXISTS ix_executable_action_grants_current
    ON {schema}.executable_action_grants(project_id, run_id, grant_id, created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_executable_action_grants_current_revision
    ON {schema}.executable_action_grants(grant_id)
    WHERE is_current;
CREATE INDEX IF NOT EXISTS ix_policy_evaluation_receipts_session
    ON {schema}.policy_evaluation_receipts(project_id, run_id, session_id, created_at);
CREATE INDEX IF NOT EXISTS ix_maf_workflow_checkpoints_latest
    ON {schema}.maf_workflow_checkpoints(project_id, run_id, session_id, store_name, created_at DESC);

CREATE OR REPLACE FUNCTION {schema}.reject_policy_receipt_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'policy evaluation receipts are immutable';
END
$$;

DROP TRIGGER IF EXISTS policy_evaluation_receipts_immutable ON {schema}.policy_evaluation_receipts;
CREATE TRIGGER policy_evaluation_receipts_immutable
    BEFORE UPDATE OR DELETE ON {schema}.policy_evaluation_receipts
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_policy_receipt_mutation();
