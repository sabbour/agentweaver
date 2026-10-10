ALTER TABLE {schema}.usage_ledger
    ADD COLUMN dispatch_id uuid,
    ADD COLUMN accounting_revision bigint,
    ADD CONSTRAINT usage_ledger_dispatch_id_nonempty
        CHECK (dispatch_id IS NULL OR dispatch_id <> '00000000-0000-0000-0000-000000000000'),
    ADD CONSTRAINT usage_ledger_accounting_revision_positive
        CHECK (accounting_revision IS NULL OR accounting_revision > 0);

CREATE UNIQUE INDEX ux_usage_ledger_run_accounting_revision
    ON {schema}.usage_ledger (tenant_id, project_id, run_id, accounting_revision)
    WHERE accounting_revision IS NOT NULL;
CREATE INDEX ix_usage_ledger_dispatch_scope
    ON {schema}.usage_ledger
       (tenant_id, project_id, run_id, dispatch_id, meter_source, price_unit, event_id)
    WHERE dispatch_id IS NOT NULL;

CREATE TABLE {schema}.usage_dispatch_source_completions (
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    dispatch_id uuid NOT NULL CHECK (dispatch_id <> '00000000-0000-0000-0000-000000000000'),
    session_id varchar(256) NOT NULL,
    runtime_instance_id uuid NOT NULL
        CHECK (runtime_instance_id <> '00000000-0000-0000-0000-000000000000'),
    registration_revision bigint NOT NULL CHECK (registration_revision > 0),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    completion_receipt_id uuid NOT NULL UNIQUE
        CHECK (completion_receipt_id <> '00000000-0000-0000-0000-000000000000'),
    completion_revision bigint NOT NULL CHECK (completion_revision > 0),
    accounting_revision bigint NOT NULL CHECK (accounting_revision > 0),
    receipt_digest varchar(64) NOT NULL CHECK (receipt_digest ~ '^[0-9a-f]{64}$'),
    canonical_manifest text NOT NULL CHECK (length(canonical_manifest) > 0),
    manifest_json jsonb NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, project_id, run_id, dispatch_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.sessions (project_id, run_id, session_id),
    CHECK (canonical_manifest::jsonb = manifest_json),
    CHECK (
        jsonb_typeof(manifest_json) = 'object'
        AND manifest_json ? 'dispatchId'
        AND jsonb_typeof(manifest_json->'dispatchId') = 'string'
        AND manifest_json->>'dispatchId' = dispatch_id::text
        AND manifest_json ? 'tenantId'
        AND jsonb_typeof(manifest_json->'tenantId') = 'string'
        AND manifest_json->>'tenantId' = tenant_id
        AND manifest_json ? 'projectId'
        AND jsonb_typeof(manifest_json->'projectId') = 'string'
        AND manifest_json->>'projectId' = project_id
        AND manifest_json ? 'runId'
        AND jsonb_typeof(manifest_json->'runId') = 'string'
        AND manifest_json->>'runId' = run_id
        AND manifest_json ? 'sessionId'
        AND jsonb_typeof(manifest_json->'sessionId') = 'string'
        AND manifest_json->>'sessionId' = session_id
        AND manifest_json ? 'runtimeInstanceId'
        AND jsonb_typeof(manifest_json->'runtimeInstanceId') = 'string'
        AND manifest_json->>'runtimeInstanceId' = runtime_instance_id::text
        AND manifest_json ? 'registrationRevision'
        AND jsonb_typeof(manifest_json->'registrationRevision') = 'number'
        AND manifest_json->>'registrationRevision' = registration_revision::text
        AND manifest_json ? 'executionFence'
        AND jsonb_typeof(manifest_json->'executionFence') = 'number'
        AND manifest_json->>'executionFence' = execution_fence::text
        AND manifest_json ? 'completionReceiptId'
        AND jsonb_typeof(manifest_json->'completionReceiptId') = 'string'
        AND manifest_json->>'completionReceiptId' = completion_receipt_id::text
        AND manifest_json ? 'completionRevision'
        AND jsonb_typeof(manifest_json->'completionRevision') = 'number'
        AND manifest_json->>'completionRevision' = completion_revision::text
        AND manifest_json ? 'receiptDigest'
        AND jsonb_typeof(manifest_json->'receiptDigest') = 'string'
        AND manifest_json->>'receiptDigest' = receipt_digest
        AND manifest_json ? 'sourceReceipts'
        AND jsonb_typeof(manifest_json->'sourceReceipts') = 'array'
    )
);

CREATE UNIQUE INDEX ux_usage_dispatch_source_completions_run_accounting_revision
    ON {schema}.usage_dispatch_source_completions
       (tenant_id, project_id, run_id, accounting_revision);
CREATE TRIGGER usage_dispatch_source_completions_append_only
    BEFORE UPDATE OR DELETE ON {schema}.usage_dispatch_source_completions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_usage_history_mutation();
CREATE TRIGGER usage_dispatch_source_completions_no_truncate
    BEFORE TRUNCATE ON {schema}.usage_dispatch_source_completions
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_usage_history_mutation();
