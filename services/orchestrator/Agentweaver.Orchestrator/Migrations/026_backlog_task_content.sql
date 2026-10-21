-- Existing ID-only tasks retain an empty title until their content is edited.
-- ALTER TABLE takes an ACCESS EXCLUSIVE lock and blocks writes until commit.
ALTER TABLE {schema}.backlog_tasks
    ADD COLUMN title text NOT NULL DEFAULT '',
    ADD COLUMN description text,
    ADD COLUMN lifecycle varchar(16) NOT NULL DEFAULT 'open',
    ADD COLUMN issue_provider_id varchar(256),
    ADD COLUMN issue_repository_owner varchar(100),
    ADD COLUMN issue_repository_name varchar(100),
    ADD COLUMN issue_number bigint,
    ADD CONSTRAINT backlog_task_title_bound CHECK (char_length(title) <= 512),
    ADD CONSTRAINT backlog_task_description_bound CHECK (char_length(description) <= 65536),
    ADD CONSTRAINT backlog_task_lifecycle CHECK (lifecycle IN ('open', 'closed')),
    ADD CONSTRAINT backlog_task_issue_identity CHECK (
        (issue_provider_id IS NULL AND issue_repository_owner IS NULL
            AND issue_repository_name IS NULL AND issue_number IS NULL)
        OR (issue_provider_id IS NOT NULL AND length(issue_provider_id) > 0
            AND issue_repository_owner IS NOT NULL AND length(issue_repository_owner) > 0
            AND issue_repository_name IS NOT NULL AND length(issue_repository_name) > 0
            AND issue_number IS NOT NULL AND issue_number > 0));

CREATE UNIQUE INDEX backlog_tasks_repository_issue_unique
    ON {schema}.backlog_tasks
        (project_id, issue_provider_id, lower(issue_repository_owner), lower(issue_repository_name), issue_number)
    WHERE issue_provider_id IS NOT NULL;
