ALTER TABLE {schema}.usage_run_cost_bindings
    ADD COLUMN binding_identity_hash varchar(64) NOT NULL DEFAULT '';

ALTER TABLE {schema}.usage_run_cost_bindings
    ADD CONSTRAINT ck_usage_run_cost_bindings_identity_hash
        CHECK (binding_identity_hash = '' OR binding_identity_hash ~ '^[0-9a-f]{64}$');

ALTER TABLE {schema}.usage_run_cost_bindings
    DROP CONSTRAINT usage_run_cost_bindings_pkey;

ALTER TABLE {schema}.usage_run_cost_bindings
    ADD CONSTRAINT pk_usage_run_cost_bindings
        PRIMARY KEY (tenant_id, project_id, run_id, meter_source, binding_identity_hash);
