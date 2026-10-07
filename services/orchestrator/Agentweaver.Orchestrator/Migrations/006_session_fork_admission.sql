ALTER TABLE {schema}.coordination_tree_commands
    ADD COLUMN fork_accepted_selection_hash char(64),
    ADD CONSTRAINT coordination_tree_commands_fork_selection_hash_check
        CHECK (fork_accepted_selection_hash IS NULL OR length(fork_accepted_selection_hash) = 64);
