CREATE TABLE "{schema}".knowledge_records (
    record_id uuid PRIMARY KEY,
    project_id varchar(256) NOT NULL,
    agent_id varchar(256) NOT NULL,
    kind varchar(32) NOT NULL CHECK (kind IN ('Memory', 'Proposal', 'Decision', 'SessionContext')),
    record_type varchar(64) NOT NULL,
    title varchar(512),
    content text NOT NULL,
    rationale text,
    importance varchar(16) NOT NULL CHECK (importance IN ('low', 'medium', 'high')),
    tags text[] NOT NULL DEFAULT '{}',
    state varchar(16) NOT NULL CHECK (state IN ('Pending', 'Active', 'Rejected', 'Archived', 'Promoted')),
    trust_state varchar(16) NOT NULL CHECK (trust_state IN ('Pending', 'Approved', 'Rejected', 'Legacy')),
    revision integer NOT NULL CHECK (revision > 0),
    current_revision_id uuid NOT NULL,
    previous_revision_id uuid,
    source_run_id varchar(256),
    source_session_id varchar(256),
    promoted_decision_id uuid,
    creator_fingerprint char(64) NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (project_id, record_id)
);

CREATE TABLE "{schema}".knowledge_revisions (
    project_id varchar(256) NOT NULL,
    record_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision > 0),
    revision_id uuid NOT NULL UNIQUE,
    previous_revision_id uuid,
    kind varchar(32) NOT NULL CHECK (kind IN ('Memory', 'Proposal', 'Decision', 'SessionContext')),
    record_type varchar(64) NOT NULL,
    title varchar(512),
    content text NOT NULL,
    rationale text,
    importance varchar(16) NOT NULL CHECK (importance IN ('low', 'medium', 'high')),
    tags text[] NOT NULL DEFAULT '{}',
    state varchar(16) NOT NULL CHECK (state IN ('Pending', 'Active', 'Rejected', 'Archived', 'Promoted')),
    trust_state varchar(16) NOT NULL CHECK (trust_state IN ('Pending', 'Approved', 'Rejected', 'Legacy')),
    source_run_id varchar(256),
    source_session_id varchar(256),
    actor_fingerprint char(64) NOT NULL,
    change_kind varchar(32) NOT NULL CHECK (change_kind IN (
        'created', 'updated', 'proposal_created', 'proposal_rejected', 'proposal_promoted')),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, record_id, revision),
    UNIQUE (project_id, record_id, revision_id),
    FOREIGN KEY (project_id, record_id)
        REFERENCES "{schema}".knowledge_records (project_id, record_id) ON DELETE RESTRICT
);

CREATE TABLE "{schema}".memory_provider_bindings (
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    project_revision bigint NOT NULL CHECK (project_revision > 0),
    project_configuration_revision bigint NOT NULL CHECK (project_configuration_revision > 0),
    context_revision varchar(256) NOT NULL,
    provider_id varchar(256) NOT NULL,
    adapter_version varchar(64) NOT NULL,
    options_schema_version integer NOT NULL CHECK (options_schema_version > 0),
    options_revision varchar(128) NOT NULL,
    resource_id varchar(256) NOT NULL,
    resource_generation bigint NOT NULL CHECK (resource_generation > 0),
    negotiated_capabilities jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id)
);

CREATE TABLE "{schema}".knowledge_write_idempotency (
    project_id varchar(256) NOT NULL,
    actor_fingerprint char(64) NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    request_fingerprint char(64) NOT NULL,
    result_kind varchar(32) NOT NULL CHECK (result_kind IN ('record', 'promotion')),
    result_record_id uuid NOT NULL,
    result_json jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, actor_fingerprint, idempotency_key)
);

CREATE INDEX knowledge_records_project_agent_state_idx
    ON "{schema}".knowledge_records (project_id, agent_id, state, updated_at DESC, record_id);
CREATE INDEX knowledge_records_project_run_idx
    ON "{schema}".knowledge_records (project_id, source_run_id, kind, state);
CREATE INDEX knowledge_revisions_history_idx
    ON "{schema}".knowledge_revisions (project_id, record_id, revision DESC);

CREATE OR REPLACE FUNCTION "{schema}".reject_knowledge_revision_mutation()
RETURNS trigger LANGUAGE plpgsql AS $body$
BEGIN
    RAISE EXCEPTION 'Knowledge revisions are immutable.' USING ERRCODE = '55000';
END
$body$;

CREATE TRIGGER knowledge_revisions_no_update
BEFORE UPDATE OR DELETE ON "{schema}".knowledge_revisions
FOR EACH ROW EXECUTE FUNCTION "{schema}".reject_knowledge_revision_mutation();

CREATE OR REPLACE FUNCTION "{schema}".reject_memory_binding_mutation()
RETURNS trigger LANGUAGE plpgsql AS $body$
BEGIN
    RAISE EXCEPTION 'Memory provider bindings are immutable.' USING ERRCODE = '55000';
END
$body$;

CREATE TRIGGER memory_provider_bindings_no_update
BEFORE UPDATE OR DELETE ON "{schema}".memory_provider_bindings
FOR EACH ROW EXECUTE FUNCTION "{schema}".reject_memory_binding_mutation();

CREATE OR REPLACE FUNCTION "{schema}".reject_knowledge_record_delete()
RETURNS trigger LANGUAGE plpgsql AS $body$
BEGIN
    RAISE EXCEPTION 'Knowledge records cannot be physically deleted.' USING ERRCODE = '55000';
END
$body$;

CREATE TRIGGER knowledge_records_no_delete
BEFORE DELETE ON "{schema}".knowledge_records
FOR EACH ROW EXECUTE FUNCTION "{schema}".reject_knowledge_record_delete();
