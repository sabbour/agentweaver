CREATE TABLE IF NOT EXISTS {schema}.backlog_projects (
    project_id varchar(256) PRIMARY KEY,
    graph_revision bigint NOT NULL DEFAULT 0 CHECK (graph_revision >= 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS {schema}.backlog_tasks (
    project_id varchar(256) NOT NULL,
    task_id varchar(256) NOT NULL,
    task_revision bigint NOT NULL DEFAULT 0 CHECK (task_revision >= 0),
    task_state varchar(16) NOT NULL DEFAULT 'backlog'
        CHECK (task_state IN ('backlog', 'ready', 'claimed')),
    is_archived boolean NOT NULL DEFAULT false,
    automation_invocation_pending boolean NOT NULL DEFAULT false,
    claim_id uuid,
    claim_phase varchar(16),
    claim_task_revision bigint,
    claim_idempotency_key varchar(128),
    claim_request_hash char(64),
    claim_run_id varchar(256),
    claim_root_session_id varchar(256),
    claim_selection_hash char(64),
    claim_execution_fence bigint,
    claim_decision_state_version bigint,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, task_id),
    FOREIGN KEY (project_id) REFERENCES {schema}.backlog_projects(project_id),
    FOREIGN KEY (project_id, claim_run_id, claim_root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id),
    CHECK (
        (claim_id IS NULL AND claim_phase IS NULL AND claim_task_revision IS NULL AND
         claim_idempotency_key IS NULL AND claim_request_hash IS NULL AND
         claim_run_id IS NULL AND claim_root_session_id IS NULL AND claim_selection_hash IS NULL AND
         claim_execution_fence IS NULL AND claim_decision_state_version IS NULL) OR
        (claim_id IS NOT NULL AND claim_phase IS NOT NULL AND
         claim_phase IN ('preparing', 'confirmed') AND
         claim_task_revision IS NOT NULL AND claim_task_revision >= 0 AND
         claim_idempotency_key IS NOT NULL AND
         claim_request_hash IS NOT NULL AND claim_request_hash ~ '^[0-9A-F]{64}$' AND
         claim_run_id IS NOT NULL AND claim_root_session_id IS NOT NULL AND
         claim_selection_hash IS NOT NULL AND claim_selection_hash ~ '^[0-9A-F]{64}$' AND
         claim_execution_fence IS NOT NULL AND claim_execution_fence > 0 AND
         claim_decision_state_version IS NOT NULL AND claim_decision_state_version > 0)
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_backlog_tasks_claim_idempotency
    ON {schema}.backlog_tasks(project_id, claim_idempotency_key)
    WHERE claim_idempotency_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_backlog_tasks_claim_run
    ON {schema}.backlog_tasks(project_id, claim_run_id)
    WHERE claim_run_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS {schema}.backlog_dependencies (
    project_id varchar(256) NOT NULL,
    task_id varchar(256) NOT NULL,
    prerequisite_task_id varchar(256) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, task_id, prerequisite_task_id),
    FOREIGN KEY (project_id, task_id)
        REFERENCES {schema}.backlog_tasks(project_id, task_id),
    FOREIGN KEY (project_id, prerequisite_task_id)
        REFERENCES {schema}.backlog_tasks(project_id, task_id),
    CHECK (task_id <> prerequisite_task_id)
);

CREATE INDEX IF NOT EXISTS ix_backlog_dependencies_prerequisite
    ON {schema}.backlog_dependencies(project_id, prerequisite_task_id, task_id);
