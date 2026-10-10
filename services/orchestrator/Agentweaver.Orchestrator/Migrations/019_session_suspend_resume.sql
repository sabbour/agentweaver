ALTER TABLE {schema}.coordination_execution_operations
    DROP CONSTRAINT IF EXISTS coordination_execution_operations_operation_kind_check;

ALTER TABLE {schema}.coordination_execution_operations
    ADD CONSTRAINT coordination_execution_operations_operation_kind_check
        CHECK (operation_kind IN ('failure', 'recovery', 'suspend', 'resume')),
    ADD COLUMN owner_execution_fence bigint,
    ADD CONSTRAINT coordination_execution_operations_owner_fence_check
        CHECK (
            (owner_execution_fence IS NULL OR owner_execution_fence > 0) AND
            (operation_kind NOT IN ('suspend', 'resume') OR owner_execution_fence IS NOT NULL)
        ),
    ADD COLUMN reserved_manifest_id uuid,
    ADD CONSTRAINT coordination_execution_operations_manifest_check
        CHECK (
            operation_kind NOT IN ('suspend', 'resume') OR
            (reserved_manifest_id IS NOT NULL AND
             reserved_manifest_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        ),
    ADD CONSTRAINT uq_coordination_execution_operations_project_manifest
        UNIQUE (project_id, reserved_manifest_id),
    ADD COLUMN operation_phase varchar(32) NOT NULL DEFAULT 'not_applicable',
    ADD CONSTRAINT coordination_execution_operations_phase_check
        CHECK (operation_phase IN (
            'not_applicable', 'reserved', 'fenced', 'draining', 'checkpointed',
            'journal_flushed', 'workspace_flushed', 'manifest_committed',
            'placement_released', 'manifest_validated', 'environment_ready',
            'egress_verified', 'fence_advanced', 'dispatched', 'interrupted')),
    ADD COLUMN phase_version bigint NOT NULL DEFAULT 0,
    ADD CONSTRAINT coordination_execution_operations_phase_version_check
        CHECK (phase_version >= 0),
    ADD COLUMN progress jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD CONSTRAINT coordination_execution_operations_progress_check
        CHECK (jsonb_typeof(progress) = 'object'),
    ADD CONSTRAINT coordination_execution_operations_suspend_resume_hash_check
        CHECK (
            operation_kind NOT IN ('suspend', 'resume') OR
            request_hash ~ '^[0-9a-f]{64}$'
        ),
    ADD CONSTRAINT uq_coordination_execution_operations_session_operation
        UNIQUE (project_id, run_id, session_id, operation_id);

CREATE TABLE {schema}.session_consistency_manifests (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    operation_id uuid NOT NULL,
    manifest_id uuid NOT NULL,
    contract_version integer NOT NULL CHECK (contract_version > 0),
    owner_execution_fence bigint NOT NULL CHECK (owner_execution_fence > 0),
    claimed_execution_fence bigint CHECK (
        claimed_execution_fence IS NULL OR claimed_execution_fence > 0),
    manifest_state varchar(16) NOT NULL
        CHECK (manifest_state IN ('suspended', 'interrupted')),
    manifest_hash char(64) NOT NULL CHECK (manifest_hash ~ '^[0-9a-f]{64}$'),
    manifest jsonb NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, session_id, operation_id),
    UNIQUE (project_id, manifest_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, session_id, operation_id)
        REFERENCES {schema}.coordination_execution_operations(
            project_id, run_id, session_id, operation_id),
    FOREIGN KEY (project_id, manifest_id)
        REFERENCES {schema}.coordination_execution_operations(project_id, reserved_manifest_id),
    CHECK ((manifest->>'contractVersion')::integer = contract_version),
    CHECK (manifest->>'manifestId' = manifest_id::text),
    CHECK (manifest->'identity'->>'projectId' = project_id),
    CHECK (manifest->'identity'->>'runId' = run_id),
    CHECK (manifest->'identity'->>'sessionId' = session_id),
    CHECK (manifest->>'state' = manifest_state),
    CHECK (
        (manifest->>'coreExecutionFence') IS NOT DISTINCT FROM
            claimed_execution_fence::text),
    CHECK (
        manifest_state <> 'suspended' OR
        (claimed_execution_fence IS NOT NULL AND
         claimed_execution_fence = owner_execution_fence)
    )
);

CREATE INDEX ix_session_consistency_manifests_owner_fence
    ON {schema}.session_consistency_manifests
        (project_id, run_id, session_id, owner_execution_fence);

CREATE OR REPLACE FUNCTION {schema}.enforce_suspend_resume_operation_phase()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    valid_transition boolean := false;
    fence_advanced boolean := false;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.operation_kind IN ('suspend', 'resume') AND
            (NEW.operation_phase <> 'reserved' OR NEW.phase_version <> 0) THEN
            RAISE EXCEPTION 'suspend/resume operations must start reserved at phase version zero';
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.operation_kind IS DISTINCT FROM OLD.operation_kind THEN
        RAISE EXCEPTION 'execution operation kind is immutable';
    END IF;
    IF OLD.operation_kind NOT IN ('suspend', 'resume') THEN
        RETURN NEW;
    END IF;

    IF NEW.project_id IS DISTINCT FROM OLD.project_id OR
        NEW.run_id IS DISTINCT FROM OLD.run_id OR
        NEW.session_id IS DISTINCT FROM OLD.session_id OR
        NEW.operation_id IS DISTINCT FROM OLD.operation_id OR
        NEW.reserved_manifest_id IS DISTINCT FROM OLD.reserved_manifest_id OR
        NEW.actor_issuer IS DISTINCT FROM OLD.actor_issuer OR
        NEW.actor_subject IS DISTINCT FROM OLD.actor_subject OR
        NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key OR
        NEW.request_hash IS DISTINCT FROM OLD.request_hash THEN
        RAISE EXCEPTION 'suspend/resume operation identity is immutable';
    END IF;

    IF NEW.phase_version <> OLD.phase_version + 1 THEN
        RAISE EXCEPTION 'suspend/resume phase version must advance exactly once';
    END IF;

    valid_transition :=
        (NEW.operation_phase = 'interrupted' AND
            OLD.operation_phase NOT IN (
                'not_applicable', 'interrupted', 'placement_released', 'dispatched')) OR
        (NEW.operation_kind = 'suspend' AND (
            (OLD.operation_phase = 'reserved' AND NEW.operation_phase = 'fenced') OR
            (OLD.operation_phase = 'fenced' AND NEW.operation_phase = 'draining') OR
            (OLD.operation_phase = 'draining' AND NEW.operation_phase = 'checkpointed') OR
            (OLD.operation_phase = 'checkpointed' AND NEW.operation_phase = 'journal_flushed') OR
            (OLD.operation_phase = 'journal_flushed' AND NEW.operation_phase = 'workspace_flushed') OR
            (OLD.operation_phase = 'workspace_flushed' AND NEW.operation_phase = 'manifest_committed') OR
            (OLD.operation_phase = 'manifest_committed' AND NEW.operation_phase = 'placement_released'))) OR
        (NEW.operation_kind = 'resume' AND (
            (OLD.operation_phase = 'reserved' AND NEW.operation_phase = 'manifest_validated') OR
            (OLD.operation_phase = 'manifest_validated' AND NEW.operation_phase = 'environment_ready') OR
            (OLD.operation_phase = 'environment_ready' AND NEW.operation_phase = 'egress_verified') OR
            (OLD.operation_phase = 'egress_verified' AND NEW.operation_phase = 'fence_advanced') OR
            (OLD.operation_phase = 'fence_advanced' AND NEW.operation_phase = 'dispatched')));

    IF NEW.operation_kind = 'resume' AND
        OLD.operation_phase = 'egress_verified' AND
        NEW.operation_phase = 'fence_advanced' THEN
        fence_advanced := NEW.owner_execution_fence = OLD.owner_execution_fence + 1;
    ELSE
        fence_advanced := NEW.owner_execution_fence = OLD.owner_execution_fence;
    END IF;

    IF NOT valid_transition OR NOT fence_advanced THEN
        RAISE EXCEPTION 'invalid suspend/resume operation phase transition';
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS coordination_execution_operations_suspend_resume_phase
    ON {schema}.coordination_execution_operations;
CREATE TRIGGER coordination_execution_operations_suspend_resume_phase
    BEFORE INSERT OR UPDATE ON {schema}.coordination_execution_operations
    FOR EACH ROW EXECUTE FUNCTION {schema}.enforce_suspend_resume_operation_phase();

CREATE OR REPLACE FUNCTION {schema}.reject_session_consistency_manifest_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'session consistency manifests are immutable';
END
$$;

DROP TRIGGER IF EXISTS session_consistency_manifests_immutable
    ON {schema}.session_consistency_manifests;
CREATE TRIGGER session_consistency_manifests_immutable
    BEFORE UPDATE OR DELETE ON {schema}.session_consistency_manifests
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_session_consistency_manifest_mutation();
DROP TRIGGER IF EXISTS session_consistency_manifests_no_truncate
    ON {schema}.session_consistency_manifests;
CREATE TRIGGER session_consistency_manifests_no_truncate
    BEFORE TRUNCATE ON {schema}.session_consistency_manifests
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_session_consistency_manifest_mutation();
