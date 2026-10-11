CREATE TABLE {schema}.maf_execution_run_guards (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    owner_revision bigint NOT NULL DEFAULT 1 CHECK (owner_revision > 0),
    copilot_soft_credit_limit numeric CHECK (copilot_soft_credit_limit IS NULL OR
        copilot_soft_credit_limit >= 0),
    copilot_hard_credit_limit numeric CHECK (copilot_hard_credit_limit IS NULL OR
        copilot_hard_credit_limit >= 0),
    cost_rate_scope_identity varchar(256) CHECK (
        cost_rate_scope_identity IS NULL OR
        (length(cost_rate_scope_identity) BETWEEN 1 AND 256 AND
         cost_rate_scope_identity = btrim(cost_rate_scope_identity))),
    cost_unit varchar(16) NOT NULL DEFAULT 'AIC' CHECK (cost_unit = 'AIC'),
    cost_scope varchar(24) NOT NULL DEFAULT 'root-run' CHECK (cost_scope = 'root-run'),
    copilot_aic_floor numeric CHECK (copilot_aic_floor IS NULL OR copilot_aic_floor >= 0),
    accounted_through_event_position bigint CHECK (accounted_through_event_position >= 0),
    usage_witness_digest char(64)
        CHECK (usage_witness_digest IS NULL OR usage_witness_digest ~ '^[0-9a-f]{64}$'),
    soft_warning_emitted boolean NOT NULL DEFAULT false,
    hard_cap_exhausted boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    CHECK (copilot_soft_credit_limit IS NULL OR copilot_hard_credit_limit IS NULL OR
        copilot_soft_credit_limit <= copilot_hard_credit_limit),
    CHECK ((accounted_through_event_position IS NULL) = (usage_witness_digest IS NULL))
);

CREATE FUNCTION {schema}.guard_maf_execution_run_guard_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'MAF execution run guards cannot be deleted';
    END IF;

    IF NEW.project_id IS DISTINCT FROM OLD.project_id OR
       NEW.run_id IS DISTINCT FROM OLD.run_id OR
       NEW.cost_unit IS DISTINCT FROM OLD.cost_unit OR
       NEW.cost_scope IS DISTINCT FROM OLD.cost_scope OR
       (OLD.cost_rate_scope_identity IS NOT NULL AND
        NEW.cost_rate_scope_identity IS DISTINCT FROM OLD.cost_rate_scope_identity) OR
       NEW.owner_revision <> OLD.owner_revision + 1 OR
       (OLD.copilot_aic_floor IS NOT NULL AND
        (NEW.copilot_aic_floor IS NULL OR NEW.copilot_aic_floor < OLD.copilot_aic_floor)) OR
       (OLD.accounted_through_event_position IS NOT NULL AND
        (NEW.accounted_through_event_position IS NULL OR
         NEW.accounted_through_event_position < OLD.accounted_through_event_position)) OR
       (NEW.accounted_through_event_position = OLD.accounted_through_event_position AND
        NEW.usage_witness_digest IS DISTINCT FROM OLD.usage_witness_digest) OR
       (OLD.soft_warning_emitted AND NOT NEW.soft_warning_emitted) OR
       (OLD.hard_cap_exhausted AND NOT NEW.hard_cap_exhausted) THEN
        RAISE EXCEPTION 'MAF execution run guard mutation is not monotone';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER maf_execution_run_guards_monotone
    BEFORE UPDATE OR DELETE ON {schema}.maf_execution_run_guards
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_maf_execution_run_guard_mutation();

CREATE TABLE {schema}.maf_execution_dispatches (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    dispatch_id uuid NOT NULL CHECK (dispatch_id <> '00000000-0000-0000-0000-000000000000'),
    root_session_id varchar(256) NOT NULL,
    child_session_id varchar(256) NOT NULL,
    work_plan_id varchar(128) NOT NULL CHECK (length(work_plan_id) > 0),
    association_id varchar(128) NOT NULL CHECK (length(association_id) > 0),
    checkpoint_store_name varchar(128) NOT NULL CHECK (checkpoint_store_name = 'coordinator-execution'),
    checkpoint_id varchar(64) NOT NULL CHECK (length(checkpoint_id) > 0),
    checkpoint_revision bigint NOT NULL CHECK (checkpoint_revision > 0),
    decision_state_version bigint NOT NULL CHECK (decision_state_version > 0),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    prompt_hash char(64) NOT NULL CHECK (prompt_hash ~ '^[0-9A-Fa-f]{64}$'),
    request_hash char(64) NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    owner_revision bigint NOT NULL DEFAULT 1 CHECK (owner_revision > 0),
    runtime_instance_id uuid NOT NULL CHECK (runtime_instance_id <> '00000000-0000-0000-0000-000000000000'),
    registration_revision bigint NOT NULL CHECK (registration_revision > 0),
    runtime_binding_hash char(64) NOT NULL CHECK (runtime_binding_hash ~ '^[0-9a-f]{64}$'),
    sdk_source_receipt_hash char(64) NOT NULL CHECK (sdk_source_receipt_hash ~ '^[0-9a-f]{64}$'),
    model_selection_reference varchar(256) NOT NULL CHECK (length(model_selection_reference) > 0),
    selected_model_id varchar(256) NOT NULL CHECK (length(selected_model_id) > 0),
    accepted_byok_descriptor jsonb CHECK (
        accepted_byok_descriptor IS NULL OR
        (jsonb_typeof(accepted_byok_descriptor) = 'object' AND
         octet_length(accepted_byok_descriptor::text) <= 16384)),
    cost_binding_identity jsonb CHECK (
        cost_binding_identity IS NULL OR
        (jsonb_typeof(cost_binding_identity) = 'object' AND
         octet_length(cost_binding_identity::text) <= 16384)),
    is_cost_producing boolean NOT NULL,
    hard_cap_serialized boolean NOT NULL DEFAULT false,
    dispatch_state varchar(32) NOT NULL
        CHECK (dispatch_state IN ('prepared', 'sending', 'terminal_pending_accounting', 'completed', 'indeterminate')),
    terminal_outcome varchar(64) CHECK (terminal_outcome IS NULL OR
        (length(terminal_outcome) > 0 AND terminal_outcome = btrim(terminal_outcome))),
    source_report_status varchar(16) NOT NULL DEFAULT 'unknown'
        CHECK (source_report_status IN ('unknown', 'partial', 'source-complete')),
    source_report_hash char(64)
        CHECK (source_report_hash IS NULL OR source_report_hash ~ '^[0-9a-f]{64}$'),
    source_report_json jsonb CHECK (
        source_report_json IS NULL OR
        (jsonb_typeof(source_report_json) = 'object' AND octet_length(source_report_json::text) <= 65536)),
    acknowledgment_references jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK (jsonb_typeof(acknowledgment_references) = 'array' AND
            octet_length(acknowledgment_references::text) <= 1048576),
    source_completion_manifest_hash char(64)
        CHECK (source_completion_manifest_hash IS NULL OR
            source_completion_manifest_hash ~ '^[0-9a-f]{64}$'),
    source_completion_manifest_json jsonb CHECK (
        source_completion_manifest_json IS NULL OR
        (jsonb_typeof(source_completion_manifest_json) = 'object' AND
         octet_length(source_completion_manifest_json::text) <= 65536)),
    accounting_status varchar(16) NOT NULL DEFAULT 'unknown'
        CHECK (accounting_status IN ('unknown', 'partial', 'unpriced', 'priced')),
    usage_witness_watermark bigint CHECK (usage_witness_watermark >= 0),
    usage_witness_digest char(64)
        CHECK (usage_witness_digest IS NULL OR usage_witness_digest ~ '^[0-9a-f]{64}$'),
    retired_through_event_position bigint CHECK (retired_through_event_position >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, dispatch_id),
    UNIQUE (project_id, run_id, root_session_id, work_plan_id, association_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, child_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, root_session_id, checkpoint_store_name, checkpoint_id)
        REFERENCES {schema}.maf_workflow_checkpoints(project_id, run_id, session_id, store_name, checkpoint_id),
    FOREIGN KEY (runtime_instance_id, registration_revision)
        REFERENCES {schema}.runtime_registration_revisions(runtime_instance_id, revision),
    CHECK (NOT hard_cap_serialized OR is_cost_producing),
    CHECK ((source_report_hash IS NULL) = (source_report_json IS NULL)),
    CHECK ((source_completion_manifest_hash IS NULL) =
        (source_completion_manifest_json IS NULL)),
    CHECK (source_report_status <> 'source-complete' OR
        source_completion_manifest_json IS NOT NULL),
    CHECK ((usage_witness_watermark IS NULL) = (usage_witness_digest IS NULL)),
    CHECK (retired_through_event_position IS NULL OR
        (dispatch_state = 'completed' AND hard_cap_serialized AND
         source_report_status = 'source-complete' AND accounting_status = 'priced' AND
         source_completion_manifest_json IS NOT NULL AND usage_witness_watermark IS NOT NULL AND
         usage_witness_digest IS NOT NULL AND
         retired_through_event_position = usage_witness_watermark)),
    CHECK (NOT hard_cap_serialized OR dispatch_state <> 'completed' OR
        retired_through_event_position IS NOT NULL)
);

CREATE UNIQUE INDEX uq_maf_execution_dispatches_open_hard_cap
    ON {schema}.maf_execution_dispatches(project_id, run_id, root_session_id)
    WHERE hard_cap_serialized AND dispatch_state <> 'completed';
CREATE INDEX ix_maf_execution_dispatches_child
    ON {schema}.maf_execution_dispatches(project_id, run_id, child_session_id, created_at DESC);

CREATE FUNCTION {schema}.guard_maf_execution_dispatch_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'MAF execution dispatches cannot be deleted';
    END IF;

    IF NEW.project_id IS DISTINCT FROM OLD.project_id OR
       NEW.run_id IS DISTINCT FROM OLD.run_id OR
       NEW.dispatch_id IS DISTINCT FROM OLD.dispatch_id OR
       NEW.root_session_id IS DISTINCT FROM OLD.root_session_id OR
       NEW.child_session_id IS DISTINCT FROM OLD.child_session_id OR
       NEW.work_plan_id IS DISTINCT FROM OLD.work_plan_id OR
       NEW.association_id IS DISTINCT FROM OLD.association_id OR
       NEW.checkpoint_store_name IS DISTINCT FROM OLD.checkpoint_store_name OR
       NEW.checkpoint_id IS DISTINCT FROM OLD.checkpoint_id OR
       NEW.checkpoint_revision IS DISTINCT FROM OLD.checkpoint_revision OR
       NEW.decision_state_version IS DISTINCT FROM OLD.decision_state_version OR
       NEW.execution_fence IS DISTINCT FROM OLD.execution_fence OR
       NEW.accepted_selection_hash IS DISTINCT FROM OLD.accepted_selection_hash OR
       NEW.prompt_hash IS DISTINCT FROM OLD.prompt_hash OR
       NEW.request_hash IS DISTINCT FROM OLD.request_hash OR
       NEW.runtime_instance_id IS DISTINCT FROM OLD.runtime_instance_id OR
       NEW.registration_revision IS DISTINCT FROM OLD.registration_revision OR
       NEW.runtime_binding_hash IS DISTINCT FROM OLD.runtime_binding_hash OR
       NEW.sdk_source_receipt_hash IS DISTINCT FROM OLD.sdk_source_receipt_hash OR
       NEW.model_selection_reference IS DISTINCT FROM OLD.model_selection_reference OR
       NEW.selected_model_id IS DISTINCT FROM OLD.selected_model_id OR
       NEW.accepted_byok_descriptor IS DISTINCT FROM OLD.accepted_byok_descriptor OR
       NEW.cost_binding_identity IS DISTINCT FROM OLD.cost_binding_identity OR
       NEW.is_cost_producing IS DISTINCT FROM OLD.is_cost_producing OR
       NEW.hard_cap_serialized IS DISTINCT FROM OLD.hard_cap_serialized OR
       NEW.created_at IS DISTINCT FROM OLD.created_at OR
       NEW.owner_revision <> OLD.owner_revision + 1 OR
       (OLD.dispatch_state = 'completed' AND NEW.dispatch_state <> 'completed') OR
       (OLD.dispatch_state <> NEW.dispatch_state AND NOT (
           (OLD.dispatch_state = 'prepared' AND NEW.dispatch_state IN ('sending', 'indeterminate')) OR
           (OLD.dispatch_state = 'sending' AND NEW.dispatch_state IN
               ('terminal_pending_accounting', 'completed', 'indeterminate')) OR
           (OLD.dispatch_state = 'terminal_pending_accounting' AND NEW.dispatch_state IN
               ('completed', 'indeterminate')) OR
           (OLD.dispatch_state = 'indeterminate' AND NEW.dispatch_state IN
               ('terminal_pending_accounting', 'completed')))) OR
       (OLD.source_report_status = 'source-complete' AND
        NEW.source_report_status <> 'source-complete') THEN
        RAISE EXCEPTION 'MAF execution dispatch mutation is not an exact forward transition';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER maf_execution_dispatches_exact_transition
    BEFORE UPDATE OR DELETE ON {schema}.maf_execution_dispatches
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_maf_execution_dispatch_mutation();

CREATE TABLE {schema}.maf_execution_output_witnesses (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    work_plan_id varchar(128) NOT NULL CHECK (length(work_plan_id) > 0),
    checkpoint_store_name varchar(128) NOT NULL CHECK (checkpoint_store_name = 'coordinator-execution'),
    checkpoint_id varchar(64) NOT NULL CHECK (length(checkpoint_id) > 0),
    checkpoint_revision bigint NOT NULL CHECK (checkpoint_revision > 0),
    decision_state_version bigint NOT NULL CHECK (decision_state_version > 0),
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    contract_version smallint NOT NULL DEFAULT 1 CHECK (contract_version = 1),
    output_set_canonical_bytes bytea NOT NULL
        CHECK (octet_length(output_set_canonical_bytes) BETWEEN 1 AND 1048576),
    output_set_sha256 char(64) NOT NULL CHECK (output_set_sha256 ~ '^[0-9a-f]{64}$'),
    owner_evidence_json jsonb NOT NULL CHECK (
        jsonb_typeof(owner_evidence_json) = 'array' AND
        octet_length(owner_evidence_json::text) <= 1048576),
    proof_sha256 char(64) NOT NULL CHECK (proof_sha256 ~ '^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, root_session_id, execution_fence, work_plan_id, checkpoint_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, root_session_id, checkpoint_store_name, checkpoint_id)
        REFERENCES {schema}.maf_workflow_checkpoints(project_id, run_id, session_id, store_name, checkpoint_id)
);

CREATE FUNCTION {schema}.reject_maf_execution_output_witness_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'MAF execution output witnesses are append-only';
END
$$;

CREATE TRIGGER maf_execution_output_witnesses_append_only
    BEFORE UPDATE OR DELETE ON {schema}.maf_execution_output_witnesses
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_maf_execution_output_witness_mutation();
CREATE TRIGGER maf_execution_output_witnesses_no_truncate
    BEFORE TRUNCATE ON {schema}.maf_execution_output_witnesses
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_maf_execution_output_witness_mutation();

CREATE FUNCTION {schema}.reject_maf_execution_truncate()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'MAF execution owner records cannot be truncated';
END
$$;

CREATE TRIGGER maf_execution_run_guards_no_truncate
    BEFORE TRUNCATE ON {schema}.maf_execution_run_guards
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_maf_execution_truncate();
CREATE TRIGGER maf_execution_dispatches_no_truncate
    BEFORE TRUNCATE ON {schema}.maf_execution_dispatches
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_maf_execution_truncate();
