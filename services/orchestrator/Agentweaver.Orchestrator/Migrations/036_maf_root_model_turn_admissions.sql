-- Stores one immutable, accepted coordinator-root model-turn admission per request.
CREATE TABLE {schema}.maf_root_model_turn_admissions (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    request_id uuid NOT NULL CHECK (request_id <> '00000000-0000-0000-0000-000000000000'),
    actor_issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL
        CHECK (accepted_selection_hash ~ '^[0-9a-f]{64}$'),
    prompt_hash char(64) NOT NULL CHECK (prompt_hash ~ '^[0-9a-f]{64}$'),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    decision_id uuid NOT NULL CHECK (decision_id <> '00000000-0000-0000-0000-000000000000'),
    decision_state_version bigint NOT NULL CHECK (decision_state_version > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, request_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, decision_id)
        REFERENCES {schema}.coordinator_decisions(project_id, run_id, decision_id)
);

CREATE TRIGGER maf_root_model_turn_admissions_no_update
    BEFORE UPDATE OR DELETE ON {schema}.maf_root_model_turn_admissions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();
