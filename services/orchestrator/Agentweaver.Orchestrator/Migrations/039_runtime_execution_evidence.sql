CREATE TABLE {schema}.maf_runtime_evidence_receipts (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    receipt_id uuid NOT NULL CHECK (receipt_id <> '00000000-0000-0000-0000-000000000000'),
    receipt_kind varchar(48) NOT NULL CHECK (
        receipt_kind IN ('numeric-limit-denial', 'replacement-comparison')),
    attempt_id uuid NOT NULL CHECK (attempt_id <> '00000000-0000-0000-0000-000000000000'),
    payload_hash char(64) NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    receipt_json jsonb NOT NULL CHECK (
        jsonb_typeof(receipt_json) = 'object' AND octet_length(receipt_json::text) <= 65536),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, receipt_id),
    UNIQUE (project_id, run_id, receipt_kind, attempt_id),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    CHECK (receipt_json->>'projectId' = project_id),
    CHECK (receipt_json->>'runId' = run_id),
    CHECK (receipt_json->>'receiptId' = receipt_id::text),
    CHECK (receipt_json->>'canonicalPayloadHash' = payload_hash)
);

CREATE FUNCTION {schema}.reject_maf_runtime_evidence_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'MAF runtime evidence receipts are append-only';
END
$$;

REVOKE ALL ON FUNCTION {schema}.reject_maf_runtime_evidence_mutation() FROM PUBLIC;

CREATE TRIGGER maf_runtime_evidence_receipts_immutable
    BEFORE UPDATE OR DELETE ON {schema}.maf_runtime_evidence_receipts
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_maf_runtime_evidence_mutation();

CREATE TRIGGER maf_runtime_evidence_receipts_no_truncate
    BEFORE TRUNCATE ON {schema}.maf_runtime_evidence_receipts
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_maf_runtime_evidence_mutation();
