ALTER TABLE {schema}.source_control_repository_pins
    ALTER COLUMN api_secret_id DROP NOT NULL,
    ALTER COLUMN api_secret_version DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS github_app_connection_id varchar(128),
    ADD COLUMN IF NOT EXISTS github_app_connection_revision bigint,
    ADD COLUMN IF NOT EXISTS github_app_installation_id bigint,
    ADD COLUMN IF NOT EXISTS github_app_permission_digest char(64),
    ADD COLUMN IF NOT EXISTS github_app_selection_hash char(64);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_source_control_pin_api_secret_pair'
          AND conrelid = '{schema}.source_control_repository_pins'::regclass
    ) THEN
        ALTER TABLE {schema}.source_control_repository_pins
            ADD CONSTRAINT ck_source_control_pin_api_secret_pair
            CHECK ((api_secret_id IS NULL) = (api_secret_version IS NULL));
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_source_control_pin_app_binding_complete'
          AND conrelid = '{schema}.source_control_repository_pins'::regclass
    ) THEN
        ALTER TABLE {schema}.source_control_repository_pins
            ADD CONSTRAINT ck_source_control_pin_app_binding_complete
            CHECK (
                (github_app_connection_id IS NULL AND
                 github_app_connection_revision IS NULL AND
                 github_app_installation_id IS NULL AND
                 github_app_permission_digest IS NULL AND
                 github_app_selection_hash IS NULL) OR
                (github_app_connection_id IS NOT NULL AND
                 github_app_connection_revision > 0 AND
                 github_app_installation_id > 0 AND
                 github_app_permission_digest ~ '^[0-9a-f]{64}$' AND
                 github_app_selection_hash ~ '^[0-9a-f]{64}$')
            );
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_source_control_pin_auth_shape'
          AND conrelid = '{schema}.source_control_repository_pins'::regclass
    ) THEN
        ALTER TABLE {schema}.source_control_repository_pins
            ADD CONSTRAINT ck_source_control_pin_auth_shape
            CHECK (
                (api_secret_id IS NOT NULL AND github_app_connection_id IS NULL) OR
                (api_secret_id IS NULL AND github_app_connection_id IS NOT NULL)
            );
    END IF;
END
$$;
