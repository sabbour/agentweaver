CREATE TABLE {schema}.coordinator_run_selection_context_versions (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    platform_runtime_revision bigint NOT NULL CHECK (platform_runtime_revision > 0),
    context_revision varchar(128) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    role_context jsonb NOT NULL CHECK (jsonb_typeof(role_context) = 'array'),
    provider_id varchar(128) NOT NULL,
    adapter_version varchar(64) NOT NULL,
    options_schema_version integer NOT NULL CHECK (options_schema_version > 0),
    options_revision varchar(128) NOT NULL,
    hosting varchar(32) NOT NULL,
    resource_id varchar(1024) NOT NULL CHECK (length(resource_id) > 0),
    resource_generation bigint NOT NULL CHECK (resource_generation > 0),
    advertised_capabilities jsonb NOT NULL CHECK (jsonb_typeof(advertised_capabilities) = 'array'),
    required_capabilities jsonb NOT NULL CHECK (jsonb_typeof(required_capabilities) = 'array'),
    negotiated_capabilities jsonb NOT NULL CHECK (jsonb_typeof(negotiated_capabilities) = 'array'),
    isolation_choices jsonb NOT NULL CHECK (jsonb_typeof(isolation_choices) = 'array'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, execution_fence),
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id)
);

INSERT INTO {schema}.coordinator_run_selection_context_versions
    (project_id, run_id, accepted_selection_hash, project_revision,
     project_configuration_revision, platform_runtime_revision, context_revision,
     execution_fence, role_context, provider_id, adapter_version,
     options_schema_version, options_revision, hosting, resource_id,
     resource_generation, advertised_capabilities, required_capabilities,
     negotiated_capabilities, isolation_choices, created_at)
SELECT project_id, run_id, accepted_selection_hash, project_revision,
       project_configuration_revision, platform_runtime_revision, context_revision,
       execution_fence, role_context, provider_id, adapter_version,
       options_schema_version, options_revision, hosting, resource_id,
       resource_generation, advertised_capabilities, required_capabilities,
       negotiated_capabilities, isolation_choices, created_at
FROM {schema}.coordinator_run_selection_contexts;

CREATE INDEX ix_coordinator_run_selection_context_versions_hash
    ON {schema}.coordinator_run_selection_context_versions(
        project_id, run_id, accepted_selection_hash, execution_fence);

CREATE OR REPLACE FUNCTION {schema}.reject_run_selection_context_version_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'accepted run selection context versions are immutable';
END
$$;

CREATE TRIGGER coordinator_run_selection_context_versions_immutable
    BEFORE UPDATE OR DELETE ON {schema}.coordinator_run_selection_context_versions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_run_selection_context_version_mutation();
