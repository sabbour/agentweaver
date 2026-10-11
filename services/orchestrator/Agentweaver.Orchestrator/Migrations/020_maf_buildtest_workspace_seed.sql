ALTER TABLE {schema}.maf_workflow_checkpoints
    ADD COLUMN workspace_seed bytea,
    ADD CONSTRAINT ck_maf_workspace_seed_bytes
        CHECK (workspace_seed IS NULL OR octet_length(workspace_seed) BETWEEN 1 AND 67188876),
    ADD CONSTRAINT fk_maf_checkpoint_parent
        FOREIGN KEY (project_id, run_id, session_id, store_name, parent_checkpoint_id)
        REFERENCES {schema}.maf_workflow_checkpoints
            (project_id, run_id, session_id, store_name, checkpoint_id)
        ON DELETE RESTRICT;
