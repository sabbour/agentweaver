-- Chief of Staff conversations and Priorities are private per-user records.
-- They never hold execution state: runs, gates, and pull requests stay with their owners,
-- and reads resolve links against those owners and current Projects authority.
-- Write blocking: none. This migration only creates new, empty tables and their indexes.
-- No existing table is altered or scanned.

-- Binds a caller's interactive run (migration 023) to a private conversation entry.
CREATE TABLE {schema}.chief_of_staff_conversations (
    conversation_id varchar(64) NOT NULL PRIMARY KEY,
    tenant_id varchar(256) NOT NULL,
    owner_issuer varchar(512) NOT NULL,
    owner_subject varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (project_id, run_id),
    FOREIGN KEY (project_id, run_id) REFERENCES {schema}.project_run_requests (project_id, run_id)
);

CREATE INDEX ix_chief_of_staff_conversations_owner
    ON {schema}.chief_of_staff_conversations
    (tenant_id, owner_issuer, owner_subject, created_at DESC, conversation_id DESC);

-- One row per owner carries the list order revision.
CREATE TABLE {schema}.chief_of_staff_priority_lists (
    tenant_id varchar(256) NOT NULL,
    owner_issuer varchar(512) NOT NULL,
    owner_subject varchar(256) NOT NULL,
    order_revision bigint NOT NULL DEFAULT 0 CHECK (order_revision >= 0),
    PRIMARY KEY (tenant_id, owner_issuer, owner_subject)
);

CREATE TABLE {schema}.chief_of_staff_priorities (
    priority_id varchar(64) NOT NULL PRIMARY KEY,
    tenant_id varchar(256) NOT NULL,
    owner_issuer varchar(512) NOT NULL,
    owner_subject varchar(256) NOT NULL,
    outcome varchar(512) NOT NULL CHECK (length(outcome) BETWEEN 1 AND 200),
    notes text CHECK (notes IS NULL OR octet_length(notes) <= 16384),
    state varchar(16) NOT NULL DEFAULT 'active' CHECK (state IN ('active', 'achieved', 'dropped')),
    checklist jsonb NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(checklist) = 'array'),
    position integer NOT NULL CHECK (position >= 0),
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    create_key_hash char(64) NOT NULL,
    create_fingerprint char(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (tenant_id, owner_issuer, owner_subject, create_key_hash),
    FOREIGN KEY (tenant_id, owner_issuer, owner_subject)
        REFERENCES {schema}.chief_of_staff_priority_lists (tenant_id, owner_issuer, owner_subject)
);

CREATE INDEX ix_chief_of_staff_priorities_owner
    ON {schema}.chief_of_staff_priorities (tenant_id, owner_issuer, owner_subject, position);

-- Typed references to owner records. A link stores identity only, never a copy of owner content.
CREATE TABLE {schema}.chief_of_staff_priority_links (
    priority_id varchar(64) NOT NULL
        REFERENCES {schema}.chief_of_staff_priorities (priority_id) ON DELETE CASCADE,
    link_id varchar(64) NOT NULL,
    kind varchar(32) NOT NULL
        CHECK (kind IN ('run', 'conversation', 'issue', 'pull_request', 'artifact')),
    project_id varchar(256) NOT NULL,
    run_id varchar(256),
    conversation_id varchar(64),
    item_number bigint CHECK (item_number IS NULL OR item_number > 0),
    artifact_id varchar(256),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (priority_id, link_id),
    CHECK (
        (kind = 'run' AND run_id IS NOT NULL AND conversation_id IS NULL AND item_number IS NULL AND artifact_id IS NULL) OR
        (kind = 'conversation' AND conversation_id IS NOT NULL AND run_id IS NULL AND item_number IS NULL AND artifact_id IS NULL) OR
        (kind IN ('issue', 'pull_request') AND item_number IS NOT NULL AND run_id IS NULL AND conversation_id IS NULL AND artifact_id IS NULL) OR
        (kind = 'artifact' AND run_id IS NOT NULL AND artifact_id IS NOT NULL AND conversation_id IS NULL AND item_number IS NULL)
    )
);

CREATE UNIQUE INDEX uq_chief_of_staff_priority_links_target
    ON {schema}.chief_of_staff_priority_links (
        priority_id, kind, project_id, coalesce(run_id, ''), coalesce(conversation_id, ''),
        coalesce(item_number, 0), coalesce(artifact_id, ''));
