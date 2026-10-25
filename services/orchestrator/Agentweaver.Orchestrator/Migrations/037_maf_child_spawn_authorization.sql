-- One durable policy allow for each exact child-spawn intent.
CREATE TABLE {schema}.maf_child_spawn_authorization_receipts (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    spawn_intent_id uuid NOT NULL
        CHECK (spawn_intent_id <> '00000000-0000-0000-0000-000000000000'),
    intent_hash char(64) NOT NULL CHECK (intent_hash ~ '^[0-9a-f]{64}$'),
    parent_session_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    context_revision varchar(128) NOT NULL,
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    platform_runtime_revision bigint NOT NULL CHECK (platform_runtime_revision > 0),
    membership_revision bigint NOT NULL CHECK (membership_revision > 0),
    role_revision bigint NOT NULL CHECK (role_revision > 0),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    plan_id varchar(128) NOT NULL,
    plan_revision bigint NOT NULL CHECK (plan_revision > 0),
    workflow_step_id varchar(128) NOT NULL,
    work_item_id varchar(256) NOT NULL,
    grant_id varchar(128) NOT NULL,
    grant_revision varchar(128) NOT NULL,
    policy_receipt_id uuid NOT NULL,
    policy_provider_id varchar(128) NOT NULL,
    policy_adapter_version varchar(64) NOT NULL,
    policy_options_schema_version integer NOT NULL CHECK (policy_options_schema_version > 0),
    policy_options_revision varchar(128) NOT NULL,
    policy_outcome varchar(16) NOT NULL CHECK (policy_outcome = 'allow'),
    policy_reason_code varchar(64) NOT NULL CHECK (policy_reason_code = 'allowed'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, spawn_intent_id),
    CONSTRAINT uq_maf_child_spawn_authorization_receipt_hash
        UNIQUE (project_id, run_id, spawn_intent_id, intent_hash),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, parent_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, grant_id, grant_revision)
        REFERENCES {schema}.executable_action_grants(project_id, run_id, grant_id, revision),
    CONSTRAINT fk_maf_child_spawn_authorization_policy_receipt
        FOREIGN KEY (project_id, run_id, policy_receipt_id)
        REFERENCES {schema}.policy_evaluation_receipts(project_id, run_id, receipt_id)
);

-- Consumption shares the exact intent hash so one identifier cannot authorize another payload.
CREATE TABLE {schema}.maf_child_spawn_authorization_consumptions (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    spawn_intent_id uuid NOT NULL,
    intent_hash char(64) NOT NULL CHECK (intent_hash ~ '^[0-9a-f]{64}$'),
    consumed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, spawn_intent_id),
    CONSTRAINT fk_maf_child_spawn_consumption_receipt
        FOREIGN KEY (project_id, run_id, spawn_intent_id, intent_hash)
        REFERENCES {schema}.maf_child_spawn_authorization_receipts(
            project_id, run_id, spawn_intent_id, intent_hash)
);

CREATE TRIGGER maf_child_spawn_authorizations_no_update
    BEFORE UPDATE OR DELETE ON {schema}.maf_child_spawn_authorization_receipts
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();

CREATE TRIGGER maf_child_spawn_consumptions_no_update
    BEFORE UPDATE OR DELETE ON {schema}.maf_child_spawn_authorization_consumptions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();
