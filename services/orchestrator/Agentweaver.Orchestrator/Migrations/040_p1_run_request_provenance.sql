ALTER TABLE {schema}.project_run_requests
    ADD COLUMN p1_setup_invocation_id uuid,
    ADD CONSTRAINT ck_project_run_request_p1_setup
        CHECK (p1_setup_invocation_id IS NULL OR
            p1_setup_invocation_id <> '00000000-0000-0000-0000-000000000000'),
    ADD CONSTRAINT uq_project_run_request_p1_setup
        UNIQUE (project_id, run_id, p1_setup_invocation_id);

CREATE TABLE {schema}.p1_run_request_provenance (
    provenance_id uuid PRIMARY KEY CHECK (provenance_id <> '00000000-0000-0000-0000-000000000000'),
    contract_version integer NOT NULL CHECK (contract_version = 1),
    hash_version integer NOT NULL CHECK (hash_version = 1),
    setup_invocation_id uuid NOT NULL CHECK (setup_invocation_id <> '00000000-0000-0000-0000-000000000000'),
    setup_receipt_hash char(64) NOT NULL CHECK (setup_receipt_hash ~ '^[0-9a-f]{64}$'),
    receipt_snapshot jsonb NOT NULL CHECK (
        jsonb_typeof(receipt_snapshot) = 'object' AND octet_length(receipt_snapshot::text) <= 32768),
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    membership_id uuid NOT NULL CHECK (membership_id <> '00000000-0000-0000-0000-000000000000'),
    membership_revision bigint NOT NULL CHECK (membership_revision > 0),
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    verified_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (setup_invocation_id),
    UNIQUE (provenance_id, setup_invocation_id, setup_receipt_hash, tenant_id, project_id, run_id,
        root_session_id, actor_issuer, actor_subject, membership_id, membership_revision),
    CONSTRAINT p1_run_request_provenance_request_fk
    FOREIGN KEY (project_id, run_id, setup_invocation_id)
        REFERENCES {schema}.project_run_requests(project_id, run_id, p1_setup_invocation_id),
    CHECK (receipt_snapshot->>'invocationId' = setup_invocation_id::text),
    CHECK (receipt_snapshot->>'issuer' = actor_issuer),
    CHECK (receipt_snapshot->>'subject' = actor_subject),
    CHECK (receipt_snapshot->>'tenantId' = tenant_id),
    CHECK (receipt_snapshot->>'membershipId' = membership_id::text),
    CHECK ((receipt_snapshot->>'membershipRevision')::bigint = membership_revision),
    CHECK (receipt_snapshot->>'projectId' = project_id),
    CHECK ((receipt_snapshot->>'projectRevision')::bigint = project_revision),
    CHECK ((receipt_snapshot->>'projectConfigurationRevision')::bigint = project_configuration_revision),
    CHECK (receipt_snapshot->>'createdByActorId' = actor_subject),
    CHECK (receipt_snapshot->>'assignmentId' ~
        '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'),
    CHECK ((receipt_snapshot->>'assignmentRevision') ~ '^[1-9][0-9]*$'),
    CHECK (receipt_snapshot->>'authorityAuditEventId' ~
        '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'),
    CHECK (receipt_snapshot ? 'ownerAssignmentId' AND
        (receipt_snapshot->'ownerAssignmentId' = 'null'::jsonb OR
         receipt_snapshot->>'ownerAssignmentId' ~
            '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')),
    CHECK (receipt_snapshot ? 'ownerAssignmentRevision' AND
        (receipt_snapshot->'ownerAssignmentRevision' = 'null'::jsonb OR
         (receipt_snapshot->>'ownerAssignmentRevision') ~ '^[1-9][0-9]*$')),
    CHECK (receipt_snapshot ? 'ownerAuthorityAuditEventId' AND
        (receipt_snapshot->'ownerAuthorityAuditEventId' = 'null'::jsonb OR
         receipt_snapshot->>'ownerAuthorityAuditEventId' ~
            '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')),
    CHECK ((receipt_snapshot->'ownerAssignmentId' = 'null'::jsonb) =
        (receipt_snapshot->'ownerAssignmentRevision' = 'null'::jsonb))
);

CREATE TABLE {schema}.p1_invocation_bindings (
    contract_version integer NOT NULL CHECK (contract_version = 1),
    core_invocation_id uuid PRIMARY KEY CHECK (core_invocation_id <> '00000000-0000-0000-0000-000000000000'),
    provenance_id uuid NOT NULL,
    setup_invocation_id uuid NOT NULL CHECK (setup_invocation_id <> '00000000-0000-0000-0000-000000000000'),
    setup_receipt_hash char(64) NOT NULL CHECK (setup_receipt_hash ~ '^[0-9a-f]{64}$'),
    tenant_id varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    membership_id uuid NOT NULL CHECK (membership_id <> '00000000-0000-0000-0000-000000000000'),
    membership_revision bigint NOT NULL CHECK (membership_revision > 0),
    accepted_selection_hash char(64) NOT NULL CHECK (accepted_selection_hash ~ '^[0-9a-f]{64}$'),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    root_state_version bigint NOT NULL CHECK (root_state_version > 0),
    accepted_at timestamptz NOT NULL,
    UNIQUE (setup_invocation_id),
    UNIQUE (project_id, run_id, root_session_id),
    CONSTRAINT p1_invocation_bindings_provenance_fk
    FOREIGN KEY (provenance_id, setup_invocation_id, setup_receipt_hash, tenant_id, project_id, run_id,
        root_session_id, actor_issuer, actor_subject, membership_id, membership_revision)
        REFERENCES {schema}.p1_run_request_provenance(provenance_id, setup_invocation_id,
            setup_receipt_hash, tenant_id, project_id, run_id, root_session_id, actor_issuer,
            actor_subject, membership_id, membership_revision),
    CONSTRAINT p1_invocation_bindings_accepted_run_fk
    FOREIGN KEY (project_id, run_id)
        REFERENCES {schema}.accepted_runs(project_id, run_id),
    CONSTRAINT p1_invocation_bindings_root_session_fk
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
);

CREATE FUNCTION {schema}.reject_p1_invocation_provenance_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'P1 invocation provenance is append-only';
END
$$;

REVOKE ALL ON FUNCTION {schema}.reject_p1_invocation_provenance_mutation() FROM PUBLIC;

CREATE TRIGGER p1_run_request_provenance_immutable
    BEFORE UPDATE OR DELETE ON {schema}.p1_run_request_provenance
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_p1_invocation_provenance_mutation();

CREATE TRIGGER p1_run_request_provenance_no_truncate
    BEFORE TRUNCATE ON {schema}.p1_run_request_provenance
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_p1_invocation_provenance_mutation();

CREATE TRIGGER p1_invocation_bindings_immutable
    BEFORE UPDATE OR DELETE ON {schema}.p1_invocation_bindings
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_p1_invocation_provenance_mutation();

CREATE TRIGGER p1_invocation_bindings_no_truncate
    BEFORE TRUNCATE ON {schema}.p1_invocation_bindings
    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.reject_p1_invocation_provenance_mutation();
