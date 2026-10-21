-- Guardrails evaluation evidence. One immutable row per content operation.
-- Rows hold digests, pins, finding counts, and policy outcomes. They never hold content or credentials.
-- operation_id is the primary key, so one operation can have only one recorded decision. The writer uses
-- ON CONFLICT DO NOTHING and then compares the stored evidence to tell a safe replay from a conflicting one.
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
    -- Execution fences start at 1, so 0 or a negative value means the caller sent a bad binding.
    CHECK (execution_fence >= 1),
    -- The content digest is an uppercase hex SHA-256. Only the digest is stored, never the content itself.
    CHECK (content_digest ~ '^[0-9A-F]{64}$'),
    -- Cap the evidence size so a bad writer can't fill the table with large documents.
    CHECK (octet_length(evidence_json::text) <= 65536),
    -- The key columns must match the same fields inside the JSON document. This stops a row whose
    -- searchable columns describe one operation while its evidence describes another.
    CHECK (
        jsonb_typeof(evidence_json) = 'object' AND
        evidence_json->>'operationId' = operation_id::text AND
        evidence_json->>'projectId' = project_id AND
        evidence_json->>'runId' = run_id AND
        evidence_json->>'contentDigest' = content_digest AND
        evidence_json->>'reasonCode' = reason_code
    )
);

-- Supports listing a run's evaluations in time order. operation_id makes the order stable when two rows
-- have the same created_at.
CREATE INDEX ix_guardrails_evaluations_project_run
    ON {schema}.guardrails_evaluations (project_id, run_id, created_at, operation_id);

-- Evidence is append-only. This function and the trigger below reject every UPDATE and DELETE, so a
-- recorded decision can't be edited or removed later, even by a role that has UPDATE or DELETE rights.
CREATE OR REPLACE FUNCTION {schema}.reject_guardrails_evaluation_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Guardrails evaluation evidence is immutable';
END
$$;

CREATE TRIGGER guardrails_evaluations_immutable
    BEFORE UPDATE OR DELETE ON {schema}.guardrails_evaluations
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_guardrails_evaluation_mutation();
