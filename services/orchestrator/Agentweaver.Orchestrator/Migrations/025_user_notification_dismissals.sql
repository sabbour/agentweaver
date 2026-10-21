-- Creates a new empty caller-state table; no existing owner table is altered.
CREATE TABLE IF NOT EXISTS {schema}.user_notification_dismissals (
    tenant_id varchar(256) NOT NULL,
    actor_issuer varchar(512) NOT NULL,
    actor_subject varchar(256) NOT NULL,
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    request_id varchar(128) NOT NULL,
    dismissed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, actor_issuer, actor_subject, project_id, run_id, request_id),
    FOREIGN KEY (project_id, run_id, request_id)
        REFERENCES {schema}.coordinator_gates(project_id, run_id, request_id)
);
