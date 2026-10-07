ALTER TABLE {schema}.coordination_tree_commands
    ADD COLUMN requested_target_session_id varchar(256),
    DROP CONSTRAINT coordination_tree_commands_command_kind_check,
    ADD CONSTRAINT coordination_tree_commands_command_kind_check
        CHECK (command_kind IN ('spawn', 'detach', 'archive', 'fork'));

CREATE UNIQUE INDEX uq_coordination_tree_commands_fork_target_reservation
    ON {schema}.coordination_tree_commands(project_id, run_id, requested_target_session_id)
    WHERE command_kind = 'fork'
      AND result ->> 'registrationState' IN ('registrationPending', 'unregistered');
