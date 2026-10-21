-- Guardrails evaluation evidence. One immutable row per content operation.
-- Rows hold digests, pins, finding counts, and policy outcomes. They never hold content or credentials.
CREATE TABLE {schema}.guardrails_evaluations (
    operation_id uuid PRIMARY KEY,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    action_id varchar(256) NOT NULL,
    grant_id varchar(256) NOT NULL,
    grant_revision varchar(256) NOT NULL,
    execution_fence bigint NOT NULL,
    stage varchar(32) NOT NULL,
    content_class varchar(32) NOT NULL,
    content_digest char(64) NOT NULL,
    reason_code varchar(64) NOT NULL,
    evidence_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (execution_fence >= 1),
    CHECK (content_digest ~ '^[0-9A-F]{64}$'),
    CHECK (octet_length(evidence_json::text) <= 65536),
    CHECK (
        jsonb_typeof(evidence_json) = 'object' AND
        evidence_json->>'operationId' = operation_id::text AND
        evidence_json->>'projectId' = project_id AND
        evidence_json->>'runId' = run_id AND
        evidence_json->>'contentDigest' = content_digest AND
        evidence_json->>'reasonCode' = reason_code
    )
);

CREATE INDEX ix_guardrails_evaluations_project_run
    ON {schema}.guardrails_evaluations (project_id, run_id, created_at, operation_id);

CREATE OR REPLACE FUNCTION {schema}.reject_guardrails_evaluation_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Guardrails evaluation evidence is immutable';
END
$$;

CREATE TRIGGER guardrails_evaluations_immutable
    BEFORE UPDATE OR DELETE ON {schema}.guardrails_evaluations
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_guardrails_evaluation_mutation();
