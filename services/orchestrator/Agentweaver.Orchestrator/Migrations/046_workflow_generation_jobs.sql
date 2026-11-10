CREATE TABLE {schema}.workflow_generation_jobs (
    job_id uuid PRIMARY KEY,
    native_request_id uuid NOT NULL UNIQUE,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    idempotency_key varchar(256) NOT NULL,
    request_fingerprint char(64) NOT NULL,
    prompt text NOT NULL CHECK (octet_length(prompt) BETWEEN 1 AND 48000),
    prompt_sha256 char(64) NOT NULL,
    expected_decision_state_version bigint NOT NULL CHECK (expected_decision_state_version > 0),
    configuration_revision bigint NOT NULL CHECK (configuration_revision > 0),
    accepted_selection_hash char(64) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    runtime_instance_id uuid NOT NULL,
    registration_revision bigint NOT NULL CHECK (registration_revision > 0),
    model_selection_reference varchar(256) NOT NULL,
    status varchar(16) NOT NULL DEFAULT 'queued'
        CHECK (status IN ('queued', 'running', 'completed', 'failed', 'cancelled')),
    execution_phase varchar(24) NOT NULL DEFAULT 'queued'
        CHECK (execution_phase IN ('queued', 'pre-send', 'send-admitted', 'quiesced')),
    failure_code varchar(128),
    failure_message varchar(512),
    result_json jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (tenant_id, project_id, actor_issuer, actor_id, idempotency_key),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    CHECK ((status = 'completed') = (result_json IS NOT NULL)),
    CHECK ((failure_code IS NULL) = (failure_message IS NULL))
);

CREATE INDEX ix_workflow_generation_jobs_unresolved
    ON {schema}.workflow_generation_jobs (project_id, run_id, execution_phase, status);

CREATE FUNCTION {schema}.guard_workflow_generation_job_insert()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    current_state varchar(16);
BEGIN
    SELECT execution_state INTO current_state
    FROM {schema}.accepted_runs
    WHERE project_id = NEW.project_id AND run_id = NEW.run_id;
    IF current_state IS DISTINCT FROM 'active' OR EXISTS (
        SELECT 1 FROM {schema}.project_run_cancellations
        WHERE project_id = NEW.project_id AND run_id = NEW.run_id
    ) THEN
        RAISE EXCEPTION 'workflow_generation_run_unavailable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER workflow_generation_job_active_run
    BEFORE INSERT ON {schema}.workflow_generation_jobs
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_workflow_generation_job_insert();

CREATE FUNCTION {schema}.close_workflow_generation_jobs_on_cancel()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE {schema}.workflow_generation_jobs
    SET status = 'cancelled',
        execution_phase = CASE
            WHEN execution_phase = 'send-admitted' THEN execution_phase
            ELSE 'quiesced'
        END,
        failure_code = 'workflow_generation_cancelled',
        failure_message = 'The run was cancelled.',
        updated_at = clock_timestamp()
    WHERE project_id = NEW.project_id AND run_id = NEW.run_id
      AND status IN ('queued', 'running');
    RETURN NEW;
END;
$$;

CREATE TRIGGER project_run_cancellation_closes_workflow_generation_jobs
    AFTER INSERT ON {schema}.project_run_cancellations
    FOR EACH ROW EXECUTE FUNCTION {schema}.close_workflow_generation_jobs_on_cancel();
