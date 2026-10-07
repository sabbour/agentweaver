CREATE TABLE {schema}.runtime_registration_heads (
    runtime_instance_id uuid PRIMARY KEY,
    binding_hash char(64) NOT NULL UNIQUE CHECK (binding_hash ~ '^[0-9a-f]{64}$'),
    current_revision bigint NOT NULL CHECK (current_revision > 0)
);

CREATE TABLE {schema}.runtime_registration_revisions (
    runtime_instance_id uuid NOT NULL REFERENCES {schema}.runtime_registration_heads(runtime_instance_id),
    revision bigint NOT NULL CHECK (revision > 0),
    binding_json varchar(16384) NOT NULL,
    binding_hash char(64) NOT NULL CHECK (binding_hash ~ '^[0-9a-f]{64}$'),
    state integer NOT NULL CHECK (state IN (0, 1)),
    expires_at timestamptz NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (runtime_instance_id, revision),
    CHECK (state = 1 OR expires_at > recorded_at)
);

CREATE FUNCTION {schema}.reject_runtime_registration_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Runtime registration revisions are append-only';
END;
$$;

CREATE TRIGGER runtime_registration_revisions_append_only
    BEFORE UPDATE OR DELETE ON {schema}.runtime_registration_revisions
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_runtime_registration_mutation();

CREATE FUNCTION {schema}.require_runtime_registration_revision_advance()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE'
       OR NEW.runtime_instance_id IS DISTINCT FROM OLD.runtime_instance_id
       OR NEW.binding_hash IS DISTINCT FROM OLD.binding_hash
       OR NEW.current_revision <> OLD.current_revision + 1 THEN
        RAISE EXCEPTION 'Runtime registration revision must advance by one';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER runtime_registration_heads_revision_advance
    BEFORE UPDATE OR DELETE ON {schema}.runtime_registration_heads
    FOR EACH ROW EXECUTE FUNCTION {schema}.require_runtime_registration_revision_advance();
