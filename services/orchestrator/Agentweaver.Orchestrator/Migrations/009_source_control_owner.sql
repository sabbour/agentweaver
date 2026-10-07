-- Source Control owner tables follow the accepted run-selection migrations.
CREATE TABLE IF NOT EXISTS {schema}.source_control_repository_pins (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    pin_id varchar(128) NOT NULL,
    session_id varchar(256) NOT NULL,
    issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    platform_runtime_revision bigint NOT NULL CHECK (platform_runtime_revision > 0),
    context_revision varchar(128) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    provider_id varchar(128) NOT NULL,
    provider_resource_id varchar(1024) NOT NULL,
    provider_resource_generation bigint NOT NULL CHECK (provider_resource_generation > 0),
    repository_owner varchar(100) NOT NULL,
    repository_name varchar(100) NOT NULL,
    provider_repository_id bigint NOT NULL CHECK (provider_repository_id > 0),
    api_secret_id varchar(256) NOT NULL,
    api_secret_version varchar(256) NOT NULL,
    checkout_secret_id varchar(256),
    checkout_secret_version varchar(256),
    webhook_secret_id varchar(256),
    webhook_secret_version varchar(256),
    pin jsonb NOT NULL CHECK (jsonb_typeof(pin) = 'object'),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, run_id, pin_id),
    UNIQUE (project_id, run_id, accepted_selection_hash),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    CHECK ((checkout_secret_id IS NULL) = (checkout_secret_version IS NULL)),
    CHECK ((webhook_secret_id IS NULL) = (webhook_secret_version IS NULL))
);

CREATE OR REPLACE FUNCTION {schema}.reject_source_control_pin_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'SourceControl repository pins are immutable';
END
$$;

DROP TRIGGER IF EXISTS source_control_repository_pins_immutable
    ON {schema}.source_control_repository_pins;
CREATE TRIGGER source_control_repository_pins_immutable
    BEFORE UPDATE OR DELETE ON {schema}.source_control_repository_pins
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_source_control_pin_mutation();

CREATE TABLE IF NOT EXISTS {schema}.source_control_merge_intents (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    intent_id varchar(128) NOT NULL,
    session_id varchar(256) NOT NULL,
    pin_id varchar(128) NOT NULL,
    issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    platform_runtime_revision bigint NOT NULL CHECK (platform_runtime_revision > 0),
    context_revision varchar(128) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    source_state_version bigint NOT NULL CHECK (source_state_version > 0),
    source_decision_id uuid NOT NULL,
    source_request_id varchar(128) NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    workflow_id varchar(128) NOT NULL,
    definition_revision varchar(128) NOT NULL,
    work_plan_id varchar(128) NOT NULL,
    assembly_request_id varchar(128) NOT NULL,
    workflow_step_id varchar(128) NOT NULL,
    approval_request_id varchar(128) NOT NULL,
    approval_request_decision_id uuid,
    approval_request_state_version bigint,
    pull_request_number bigint NOT NULL CHECK (pull_request_number > 0),
    head_branch varchar(256) NOT NULL,
    head_sha varchar(64) NOT NULL,
    base_branch varchar(256) NOT NULL,
    base_sha varchar(64) NOT NULL,
    merge_method varchar(16) NOT NULL CHECK (merge_method IN ('merge', 'squash', 'rebase')),
    intent_request jsonb NOT NULL CHECK (jsonb_typeof(intent_request) = 'object'),
    intent_state varchar(24) NOT NULL CHECK (intent_state IN (
        'approval_pending', 'approved', 'rejected', 'merge_started',
        'merged', 'outcome_uncertain', 'conflict', 'stale', 'revoked')),
    approval_decision_id uuid,
    approval_state_version bigint,
    approval_receipt jsonb,
    merge_sha varchar(64),
    last_failure_code varchar(64),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, intent_id),
    UNIQUE (project_id, run_id, idempotency_key),
    UNIQUE (project_id, run_id, approval_request_id),
    FOREIGN KEY (project_id, run_id, pin_id)
        REFERENCES {schema}.source_control_repository_pins(project_id, run_id, pin_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, source_decision_id)
        REFERENCES {schema}.coordinator_decisions(project_id, run_id, decision_id),
    FOREIGN KEY (project_id, run_id, approval_decision_id)
        REFERENCES {schema}.coordinator_decisions(project_id, run_id, decision_id),
    FOREIGN KEY (project_id, run_id, approval_request_decision_id)
        REFERENCES {schema}.coordinator_decisions(project_id, run_id, decision_id),
    CHECK ((approval_request_decision_id IS NULL) = (approval_request_state_version IS NULL)),
    CHECK ((approval_decision_id IS NULL) = (approval_state_version IS NULL)),
    CHECK ((approval_decision_id IS NULL) = (approval_receipt IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_source_control_merge_intents_active
    ON {schema}.source_control_merge_intents(project_id, run_id, intent_state, created_at DESC);

CREATE OR REPLACE FUNCTION {schema}.reject_source_control_merge_request_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'SourceControl merge requests are immutable';
    END IF;
    IF OLD.approval_request_decision_id IS NOT NULL AND
       ROW(NEW.approval_request_decision_id, NEW.approval_request_state_version) IS DISTINCT FROM
       ROW(OLD.approval_request_decision_id, OLD.approval_request_state_version) THEN
        RAISE EXCEPTION 'SourceControl approval requests are immutable once recorded';
    END IF;
    IF ROW(
        NEW.project_id, NEW.run_id, NEW.intent_id, NEW.session_id, NEW.pin_id,
        NEW.issuer, NEW.actor_id, NEW.tenant_id, NEW.accepted_selection_hash,
        NEW.project_revision, NEW.project_configuration_revision, NEW.platform_runtime_revision,
        NEW.context_revision, NEW.execution_fence, NEW.source_state_version,
        NEW.source_decision_id, NEW.source_request_id, NEW.idempotency_key,
        NEW.workflow_id, NEW.definition_revision, NEW.work_plan_id,
        NEW.assembly_request_id, NEW.workflow_step_id, NEW.approval_request_id,
        NEW.pull_request_number, NEW.head_branch, NEW.head_sha, NEW.base_branch,
        NEW.base_sha, NEW.merge_method, NEW.intent_request, NEW.created_at
    ) IS DISTINCT FROM ROW(
        OLD.project_id, OLD.run_id, OLD.intent_id, OLD.session_id, OLD.pin_id,
        OLD.issuer, OLD.actor_id, OLD.tenant_id, OLD.accepted_selection_hash,
        OLD.project_revision, OLD.project_configuration_revision, OLD.platform_runtime_revision,
        OLD.context_revision, OLD.execution_fence, OLD.source_state_version,
        OLD.source_decision_id, OLD.source_request_id, OLD.idempotency_key,
        OLD.workflow_id, OLD.definition_revision, OLD.work_plan_id,
        OLD.assembly_request_id, OLD.workflow_step_id, OLD.approval_request_id,
        OLD.pull_request_number, OLD.head_branch, OLD.head_sha, OLD.base_branch,
        OLD.base_sha, OLD.merge_method, OLD.intent_request, OLD.created_at
    ) THEN
        RAISE EXCEPTION 'SourceControl merge request facts are immutable';
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS source_control_merge_intents_immutable
    ON {schema}.source_control_merge_intents;
CREATE TRIGGER source_control_merge_intents_immutable
    BEFORE UPDATE OR DELETE ON {schema}.source_control_merge_intents
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_source_control_merge_request_mutation();

CREATE TABLE IF NOT EXISTS {schema}.source_control_webhook_deliveries (
    repository_owner varchar(100) NOT NULL,
    repository_name varchar(100) NOT NULL,
    delivery_id varchar(128) NOT NULL,
    event_name varchar(64) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    pin_id varchar(128) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    payload_sha256 char(64) NOT NULL,
    delivery_state varchar(24) NOT NULL CHECK (delivery_state IN ('accepted', 'completed', 'rejected')),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (repository_owner, repository_name, delivery_id),
    FOREIGN KEY (project_id, run_id, pin_id)
        REFERENCES {schema}.source_control_repository_pins(project_id, run_id, pin_id)
);

ALTER TABLE {schema}.executable_action_grants
    ADD COLUMN IF NOT EXISTS source_control_intent_id varchar(128);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_executable_action_grants_source_control_intent'
          AND conrelid = '{schema}.executable_action_grants'::regclass
    ) THEN
        ALTER TABLE {schema}.executable_action_grants
            ADD CONSTRAINT fk_executable_action_grants_source_control_intent
            FOREIGN KEY (project_id, run_id, source_control_intent_id)
            REFERENCES {schema}.source_control_merge_intents(project_id, run_id, intent_id);
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_executable_action_grants_source_control_intent'
          AND conrelid = '{schema}.executable_action_grants'::regclass
    ) THEN
        ALTER TABLE {schema}.executable_action_grants
            ADD CONSTRAINT ck_executable_action_grants_source_control_intent
            CHECK (
                source_control_intent_id IS NULL OR
                (purpose = 'source-control.merge' AND action_ids ? 'source_control.merge')
            );
    END IF;
END
$$;
