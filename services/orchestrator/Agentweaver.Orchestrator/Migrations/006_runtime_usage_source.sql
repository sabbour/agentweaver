CREATE TABLE {schema}.runtime_sdk_sources (
    runtime_instance_id uuid PRIMARY KEY,
    registration_revision bigint NOT NULL,
    source_grant_id uuid NOT NULL,
    configuration_hash char(64) NOT NULL CHECK (configuration_hash ~ '^[0-9a-f]{64}$'),
    canonical_payload_hash char(64) NOT NULL CHECK (canonical_payload_hash ~ '^[0-9a-f]{64}$'),
    receipt_json varchar(32768) NOT NULL,
    recorded_at timestamptz NOT NULL,
    FOREIGN KEY (runtime_instance_id, registration_revision)
        REFERENCES {schema}.runtime_registration_revisions(runtime_instance_id, revision)
);

CREATE TABLE {schema}.runtime_usage_observations (
    receipt_id uuid PRIMARY KEY,
    runtime_instance_id uuid NOT NULL REFERENCES {schema}.runtime_sdk_sources(runtime_instance_id),
    event_id uuid NOT NULL UNIQUE,
    sdk_event_id uuid NOT NULL,
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    canonical_payload_hash char(64) NOT NULL CHECK (canonical_payload_hash ~ '^[0-9a-f]{64}$'),
    receipt_json varchar(65536) NOT NULL,
    recorded_at timestamptz NOT NULL,
    UNIQUE (runtime_instance_id, sdk_event_id)
);
CREATE INDEX runtime_usage_observations_run
    ON {schema}.runtime_usage_observations(tenant_id, project_id, run_id, session_id);

CREATE FUNCTION {schema}.reject_runtime_usage_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Native runtime usage source records are append-only';
END;
$$;

CREATE TRIGGER runtime_sdk_sources_append_only
    BEFORE UPDATE OR DELETE ON {schema}.runtime_sdk_sources
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_runtime_usage_mutation();
CREATE TRIGGER runtime_usage_observations_append_only
    BEFORE UPDATE OR DELETE ON {schema}.runtime_usage_observations
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_runtime_usage_mutation();

CREATE TRIGGER runtime_sdk_sources_no_truncate
    BEFORE TRUNCATE ON {schema}.runtime_sdk_sources
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_runtime_usage_mutation();
CREATE TRIGGER runtime_usage_observations_no_truncate
    BEFORE TRUNCATE ON {schema}.runtime_usage_observations
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_runtime_usage_mutation();
