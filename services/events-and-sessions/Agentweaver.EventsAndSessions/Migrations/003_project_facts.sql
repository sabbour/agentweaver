-- Migration 3 adds receipt-backed accepted-effect facts.
CREATE TABLE IF NOT EXISTS {schema}.project_fact_streams (
    project_id varchar(256) PRIMARY KEY,
    last_position bigint NOT NULL CHECK (last_position > 0)
);

CREATE TABLE IF NOT EXISTS {schema}.project_facts (
    receipt_id uuid PRIMARY KEY,
    project_id varchar(256) NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    fact_id uuid NOT NULL UNIQUE,
    schema_version integer NOT NULL CHECK (schema_version > 0),
    event_version integer NOT NULL CHECK (event_version > 0),
    run_id varchar(256) NOT NULL,
    effect_id uuid NOT NULL,
    record_id uuid NOT NULL,
    record_version integer NOT NULL CHECK (record_version > 0),
    issuer varchar(2048) NOT NULL,
    subject varchar(256) NOT NULL,
    tenant_id varchar(256) NOT NULL,
    accepted_at timestamptz NOT NULL,
    receipt jsonb NOT NULL,
    acknowledgment jsonb NOT NULL,
    inbox_consumer text NOT NULL DEFAULT 'events-and-sessions.accepted-effects',
    inbox_message_id text NOT NULL,
    UNIQUE (project_id, sequence),
    UNIQUE (inbox_consumer, inbox_message_id),
    FOREIGN KEY (inbox_consumer, inbox_message_id)
        REFERENCES {schema}.consumer_inbox_receipts (consumer_id, message_id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_project_facts_address
    ON {schema}.project_facts (project_id, sequence);

CREATE OR REPLACE FUNCTION {schema}.reject_project_fact_mutation()
RETURNS trigger LANGUAGE plpgsql AS $body$
BEGIN
    RAISE EXCEPTION 'Project facts are immutable.' USING ERRCODE = '55000';
END
$body$;

CREATE TRIGGER project_facts_no_update_or_delete
BEFORE UPDATE OR DELETE ON {schema}.project_facts
FOR EACH ROW EXECUTE FUNCTION {schema}.reject_project_fact_mutation();
