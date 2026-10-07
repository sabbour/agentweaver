ALTER TABLE {schema}.coordination_sessions
    ADD COLUMN work_plan_item_id varchar(256),
    ADD CONSTRAINT ck_coordination_runtime_work_plan_item
        CHECK (work_plan_item_id IS NULL OR
            (parent_session_id IS NOT NULL AND length(work_plan_item_id) > 0));
