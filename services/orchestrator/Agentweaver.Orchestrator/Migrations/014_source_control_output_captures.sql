CREATE TABLE {schema}.source_control_output_captures (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    session_id varchar(256) NOT NULL,
    capture_id varchar(42) NOT NULL,
    event_id uuid NOT NULL,
    pin_id varchar(128) NOT NULL,
    issuer varchar(512) NOT NULL,
    actor_id varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_selection_hash char(64) NOT NULL,
    workspace_id varchar(128) NOT NULL,
    workspace_incarnation_id uuid NOT NULL,
    repository_id varchar(256) NOT NULL,
    resource_generation bigint NOT NULL CHECK (resource_generation > 0),
    base_sha char(40) NOT NULL,
    output_tree_sha char(40) NOT NULL,
    manifest_sha256 char(64) NOT NULL,
    manifest_byte_length bigint NOT NULL CHECK (manifest_byte_length BETWEEN 1 AND 33554432),
    manifest_bytes bytea NOT NULL CHECK (octet_length(manifest_bytes) BETWEEN 1 AND 33554432),
    patch_sha256 char(64) NOT NULL,
    patch_byte_length bigint NOT NULL CHECK (patch_byte_length BETWEEN 0 AND 8388608),
    patch_bytes bytea NOT NULL CHECK (octet_length(patch_bytes) BETWEEN 0 AND 8388608),
    package_sha256 char(64) NOT NULL,
    package_byte_length bigint NOT NULL CHECK (package_byte_length BETWEEN 12 AND 67188876),
    capture_state varchar(16) NOT NULL DEFAULT 'pending'
        CHECK (capture_state IN ('pending', 'admitted')),
    object_key varchar(1024),
    event_position bigint CHECK (event_position > 0),
    captured_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    admitted_at timestamptz,
    PRIMARY KEY (project_id, run_id, session_id, capture_id),
    UNIQUE (project_id, run_id, event_id),
    FOREIGN KEY (project_id, run_id, pin_id)
        REFERENCES {schema}.source_control_repository_pins(project_id, run_id, pin_id),
    FOREIGN KEY (project_id, run_id, session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    CHECK (capture_id ~ '^sc-output-[0-9a-f]{32}$'),
    CHECK (accepted_selection_hash ~ '^[0-9A-Fa-f]{64}$'),
    CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (patch_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK (package_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK ((capture_state = 'pending' AND object_key IS NULL AND event_position IS NULL AND admitted_at IS NULL) OR
           (capture_state = 'admitted' AND object_key IS NOT NULL AND event_position IS NOT NULL AND admitted_at IS NOT NULL))
);

CREATE INDEX ix_source_control_output_captures_session
    ON {schema}.source_control_output_captures
        (project_id, run_id, session_id, captured_at DESC, capture_id);

CREATE OR REPLACE FUNCTION {schema}.guard_source_control_output_capture_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'SourceControl output captures are immutable';
    END IF;

    IF OLD.capture_state = 'pending' AND NEW.capture_state = 'admitted' AND
       OLD.project_id IS NOT DISTINCT FROM NEW.project_id AND
       OLD.run_id IS NOT DISTINCT FROM NEW.run_id AND
       OLD.session_id IS NOT DISTINCT FROM NEW.session_id AND
       OLD.capture_id IS NOT DISTINCT FROM NEW.capture_id AND
       OLD.event_id IS NOT DISTINCT FROM NEW.event_id AND
       OLD.pin_id IS NOT DISTINCT FROM NEW.pin_id AND
       OLD.issuer IS NOT DISTINCT FROM NEW.issuer AND
       OLD.actor_id IS NOT DISTINCT FROM NEW.actor_id AND
       OLD.tenant_id IS NOT DISTINCT FROM NEW.tenant_id AND
       OLD.accepted_selection_hash IS NOT DISTINCT FROM NEW.accepted_selection_hash AND
       OLD.workspace_id IS NOT DISTINCT FROM NEW.workspace_id AND
       OLD.workspace_incarnation_id IS NOT DISTINCT FROM NEW.workspace_incarnation_id AND
       OLD.repository_id IS NOT DISTINCT FROM NEW.repository_id AND
       OLD.resource_generation IS NOT DISTINCT FROM NEW.resource_generation AND
       OLD.base_sha IS NOT DISTINCT FROM NEW.base_sha AND
       OLD.output_tree_sha IS NOT DISTINCT FROM NEW.output_tree_sha AND
       OLD.manifest_sha256 IS NOT DISTINCT FROM NEW.manifest_sha256 AND
       OLD.manifest_byte_length IS NOT DISTINCT FROM NEW.manifest_byte_length AND
       OLD.manifest_bytes IS NOT DISTINCT FROM NEW.manifest_bytes AND
       OLD.patch_sha256 IS NOT DISTINCT FROM NEW.patch_sha256 AND
       OLD.patch_byte_length IS NOT DISTINCT FROM NEW.patch_byte_length AND
       OLD.patch_bytes IS NOT DISTINCT FROM NEW.patch_bytes AND
       OLD.package_sha256 IS NOT DISTINCT FROM NEW.package_sha256 AND
       OLD.package_byte_length IS NOT DISTINCT FROM NEW.package_byte_length AND
       OLD.captured_at IS NOT DISTINCT FROM NEW.captured_at AND
       OLD.object_key IS NULL AND OLD.event_position IS NULL AND OLD.admitted_at IS NULL AND
       NEW.object_key IS NOT NULL AND NEW.event_position IS NOT NULL AND NEW.admitted_at IS NOT NULL THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'SourceControl output captures are immutable';
END
$$;

CREATE TRIGGER source_control_output_captures_immutable
    BEFORE UPDATE OR DELETE ON {schema}.source_control_output_captures
    FOR EACH ROW EXECUTE FUNCTION {schema}.guard_source_control_output_capture_mutation();
