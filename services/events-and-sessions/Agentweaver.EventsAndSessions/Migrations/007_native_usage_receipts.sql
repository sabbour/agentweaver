CREATE TABLE {schema}.usage_run_cost_bindings (
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    meter_source varchar(256) NOT NULL,
    binding_json varchar(32768),
    unpriced_reason varchar(1024),
    PRIMARY KEY (tenant_id, project_id, run_id, meter_source),
    CHECK ((binding_json IS NOT NULL AND unpriced_reason IS NULL)
        OR (binding_json IS NULL AND unpriced_reason IS NOT NULL))
);

CREATE TABLE {schema}.usage_source_receipts (
    source_receipt_id uuid PRIMARY KEY,
    event_id uuid NOT NULL UNIQUE REFERENCES {schema}.usage_ledger(event_id),
    source_payload_hash char(64) NOT NULL CHECK (source_payload_hash ~ '^[0-9a-f]{64}$'),
    source_receipt_json varchar(65536) NOT NULL,
    accounting_receipt_json varchar(32768) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE FUNCTION {schema}.reject_usage_receipt_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Native usage bindings and accounting receipts are append-only';
END;
$$;
CREATE TRIGGER usage_run_cost_bindings_append_only
    BEFORE UPDATE OR DELETE ON {schema}.usage_run_cost_bindings
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_usage_receipt_mutation();
CREATE TRIGGER usage_source_receipts_append_only
    BEFORE UPDATE OR DELETE ON {schema}.usage_source_receipts
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_usage_receipt_mutation();

CREATE TRIGGER usage_run_cost_bindings_no_truncate
    BEFORE TRUNCATE ON {schema}.usage_run_cost_bindings
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_usage_receipt_mutation();
CREATE TRIGGER usage_source_receipts_no_truncate
    BEFORE TRUNCATE ON {schema}.usage_source_receipts
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_usage_receipt_mutation();
