ALTER TABLE {schema}.source_control_pull_request_operations
    ADD COLUMN tenant_id varchar(256),
    ADD COLUMN effect_fingerprint char(64),
    ADD COLUMN original_authorization_fingerprint char(64),
    ADD COLUMN effect_state varchar(16),
    ADD COLUMN result_fingerprint char(64);

UPDATE {schema}.source_control_pull_request_operations AS operation
SET tenant_id = pin.tenant_id,
    effect_fingerprint = operation.request_fingerprint,
    effect_state = CASE
        WHEN operation.pull_request_number IS NULL THEN 'pending'
        ELSE 'committed'
    END
FROM {schema}.source_control_repository_pins AS pin
WHERE pin.project_id = operation.project_id
  AND pin.run_id = operation.run_id
  AND pin.pin_id = operation.pin_id;

ALTER TABLE {schema}.source_control_pull_request_operations
    ALTER COLUMN tenant_id SET NOT NULL,
    ALTER COLUMN effect_fingerprint SET NOT NULL,
    ALTER COLUMN effect_state SET NOT NULL,
    ADD CONSTRAINT ck_source_control_pull_request_effect_hash
        CHECK (effect_fingerprint ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_source_control_pull_request_authorization_hash
        CHECK (original_authorization_fingerprint IS NULL OR
            original_authorization_fingerprint ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_source_control_pull_request_result_hash
        CHECK (result_fingerprint IS NULL OR result_fingerprint ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_source_control_pull_request_effect_state
        CHECK (effect_state IN ('pending', 'indeterminate', 'committed')),
    ADD CONSTRAINT ck_source_control_pull_request_effect_result
        CHECK ((effect_state = 'committed') = (pull_request_number IS NOT NULL));
