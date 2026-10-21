-- Records the promotion of a confirmed coordinator work plan into backlog tasks, and the origin of each promoted task.
-- Tasks captured directly have no origin row. backlog_tasks is not altered.
-- Creating the foreign keys takes SHARE ROW EXCLUSIVE locks on backlog_projects, backlog_tasks,
-- and coordination_sessions, which block writes to those tables until commit.
CREATE TABLE {schema}.backlog_plan_promotions (
    promotion_id uuid PRIMARY KEY
        CHECK (promotion_id <> '00000000-0000-0000-0000-000000000000'),
    project_id varchar(256) NOT NULL,
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    work_plan_id varchar(256) NOT NULL CHECK (length(work_plan_id) > 0),
    workflow_id varchar(256) NOT NULL CHECK (length(workflow_id) > 0),
    workflow_revision varchar(256) NOT NULL CHECK (length(workflow_revision) > 0),
    decision_state_version bigint NOT NULL CHECK (decision_state_version > 0),
    execution_fence bigint NOT NULL CHECK (execution_fence > 0),
    graph_revision bigint NOT NULL CHECK (graph_revision > 0),
    idempotency_key varchar(128) NOT NULL CHECK (length(idempotency_key) > 0),
    request_hash char(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
    task_count integer NOT NULL CHECK (task_count > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (project_id) REFERENCES {schema}.backlog_projects(project_id),
    FOREIGN KEY (project_id, run_id, root_session_id)
        REFERENCES {schema}.coordination_sessions(project_id, run_id, session_id)
);

CREATE UNIQUE INDEX uq_backlog_plan_promotions_plan
    ON {schema}.backlog_plan_promotions(project_id, run_id, work_plan_id);

CREATE UNIQUE INDEX uq_backlog_plan_promotions_idempotency
    ON {schema}.backlog_plan_promotions(project_id, idempotency_key);

CREATE TABLE {schema}.backlog_task_origins (
    project_id varchar(256) NOT NULL,
    task_id varchar(256) NOT NULL,
    promotion_id uuid NOT NULL REFERENCES {schema}.backlog_plan_promotions(promotion_id),
    run_id varchar(256) NOT NULL,
    root_session_id varchar(256) NOT NULL,
    work_plan_id varchar(256) NOT NULL CHECK (length(work_plan_id) > 0),
    work_item_id varchar(256) NOT NULL CHECK (length(work_item_id) > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (project_id, task_id),
    FOREIGN KEY (project_id, task_id) REFERENCES {schema}.backlog_tasks(project_id, task_id)
);

CREATE UNIQUE INDEX uq_backlog_task_origins_item
    ON {schema}.backlog_task_origins(project_id, run_id, work_plan_id, work_item_id);

CREATE INDEX ix_backlog_task_origins_promotion
    ON {schema}.backlog_task_origins(promotion_id);
