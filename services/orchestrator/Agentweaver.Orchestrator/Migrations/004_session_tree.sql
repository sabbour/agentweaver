ALTER TABLE {schema}.coordination_sessions
    ADD COLUMN root_session_id varchar(256),
    ADD COLUMN node_kind varchar(32) NOT NULL DEFAULT 'child_work',
    ADD COLUMN detached boolean NOT NULL DEFAULT false,
    ADD COLUMN archived_at timestamptz,
    ADD COLUMN work_plan_item_id varchar(256);

WITH RECURSIVE lineage (
    project_id, run_id, node_session_id, ancestor_session_id, parent_session_id, path
) AS (
    SELECT project_id, run_id, session_id, session_id, parent_session_id,
        ARRAY[session_id]::varchar(256)[]
    FROM {schema}.coordination_sessions
    UNION ALL
    SELECT lineage.project_id, lineage.run_id, lineage.node_session_id,
        parent.session_id, parent.parent_session_id,
        (lineage.path || parent.session_id)::varchar(256)[]
    FROM lineage
    JOIN {schema}.coordination_sessions AS parent
      ON parent.project_id = lineage.project_id
     AND parent.run_id = lineage.run_id
     AND parent.session_id = lineage.parent_session_id
    WHERE lineage.parent_session_id IS NOT NULL
      AND NOT parent.session_id = ANY(lineage.path)
),
roots AS (
    SELECT DISTINCT ON (project_id, run_id, node_session_id)
        project_id, run_id, node_session_id, ancestor_session_id
    FROM lineage
    WHERE parent_session_id IS NULL
    ORDER BY project_id, run_id, node_session_id
)
UPDATE {schema}.coordination_sessions AS session
SET root_session_id = roots.ancestor_session_id,
    node_kind = CASE WHEN session.parent_session_id IS NULL THEN 'coordinator' ELSE session.node_kind END
FROM roots
WHERE session.project_id = roots.project_id
  AND session.run_id = roots.run_id
  AND session.session_id = roots.node_session_id;

ALTER TABLE {schema}.coordination_sessions
    ALTER COLUMN root_session_id SET NOT NULL,
    ADD CONSTRAINT coordination_sessions_node_kind_check
        CHECK (node_kind IN ('coordinator', 'child_work', 'scribe', 'operator_chat', 'child_run')),
    ADD CONSTRAINT coordination_sessions_root_fk
        FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    DROP CONSTRAINT coordination_sessions_lifecycle_state_check,
    ADD CONSTRAINT coordination_sessions_lifecycle_state_check
        CHECK (lifecycle_state IN ('active', 'cancelled', 'completed', 'archived')),
    ADD CONSTRAINT coordination_sessions_archive_timestamp_check
        CHECK ((lifecycle_state = 'archived') = (archived_at IS NOT NULL)),
    ADD CONSTRAINT coordination_sessions_work_plan_item_kind_check
        CHECK (work_plan_item_id IS NULL OR node_kind = 'child_work');

CREATE INDEX ix_coordination_sessions_tree
    ON {schema}.coordination_sessions(project_id, run_id, root_session_id, created_at, session_id);

CREATE TABLE {schema}.coordination_tree_commands (
    command_id uuid NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    source_session_id varchar(256) NOT NULL,
    target_session_id varchar(256),
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    command_kind varchar(16) NOT NULL CHECK (command_kind IN ('spawn', 'detach', 'archive')),
    idempotency_key varchar(128) NOT NULL,
    command_hash char(64) NOT NULL,
    result jsonb NOT NULL CHECK (jsonb_typeof(result) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, command_id),
    UNIQUE (project_id, run_id, source_session_id, actor_issuer, actor_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, source_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, target_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
);

CREATE TABLE {schema}.coordination_idle_subscriptions (
    subscription_id uuid NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    target_session_id varchar(256) NOT NULL,
    subscriber_session_id varchar(256) NOT NULL,
    writer_issuer varchar(512) NOT NULL,
    writer_subject varchar(256) NOT NULL,
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    mode varchar(8) NOT NULL CHECK (mode IN ('once', 'always')),
    idempotency_key varchar(128) NOT NULL,
    command_hash char(64) NOT NULL,
    active boolean NOT NULL DEFAULT true,
    last_notified_turn bigint NOT NULL DEFAULT 0 CHECK (last_notified_turn >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, subscription_id),
    UNIQUE (
        project_id, run_id, target_session_id, subscriber_session_id,
        writer_issuer, writer_subject, idempotency_key),
    FOREIGN KEY (project_id, run_id, target_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    FOREIGN KEY (project_id, run_id, subscriber_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
);

CREATE INDEX ix_coordination_idle_subscriptions_active
    ON {schema}.coordination_idle_subscriptions(project_id, run_id, target_session_id, execution_fence)
    WHERE active;

CREATE TABLE {schema}.coordination_idle_notifications (
    notification_id uuid NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    subscription_id uuid NOT NULL,
    turn_ordinal bigint NOT NULL CHECK (turn_ordinal > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, run_id, notification_id),
    UNIQUE (project_id, run_id, subscription_id, turn_ordinal),
    FOREIGN KEY (project_id, run_id, subscription_id)
        REFERENCES {schema}.coordination_idle_subscriptions(project_id, run_id, subscription_id)
);
