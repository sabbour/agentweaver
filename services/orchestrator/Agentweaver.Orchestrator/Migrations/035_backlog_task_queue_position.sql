-- Stores deterministic order within each project backlog queue.
ALTER TABLE {schema}.backlog_tasks
    ADD COLUMN queue_position bigint;

WITH ranked_tasks AS (
    SELECT project_id, task_id,
           row_number() OVER (PARTITION BY project_id, task_state ORDER BY task_id) - 1 AS queue_position
    FROM {schema}.backlog_tasks
)
UPDATE {schema}.backlog_tasks AS task
SET queue_position = ranked_tasks.queue_position
FROM ranked_tasks
WHERE task.project_id = ranked_tasks.project_id
  AND task.task_id = ranked_tasks.task_id;

ALTER TABLE {schema}.backlog_tasks
    ALTER COLUMN queue_position SET NOT NULL,
    ADD CONSTRAINT ck_backlog_tasks_queue_position_nonnegative CHECK (queue_position >= 0);

CREATE INDEX ix_backlog_tasks_project_state_position
    ON {schema}.backlog_tasks (project_id, task_state, queue_position, task_id);
