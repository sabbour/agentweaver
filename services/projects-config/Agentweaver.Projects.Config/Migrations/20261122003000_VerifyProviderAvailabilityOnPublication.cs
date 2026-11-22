using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class VerifyProviderAvailabilityOnPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_operations_shape",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.AddColumn<string>(
                name: "provider_state",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rate_card_id",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rate_card_pin_hash",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rate_card_version",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_operations_provider_state",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                sql: "provider_state IS NULL OR provider_state IN ('Draft', 'Available')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_operations_rate_card",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                sql: "(provider_state = 'Available' AND operation_kind = 'Publish' AND rate_card_id IS NOT NULL AND length(rate_card_id) > 0 AND rate_card_version IS NOT NULL AND length(rate_card_version) > 0 AND rate_card_pin_hash ~ '^[0-9a-f]{64}$') OR (provider_state IS DISTINCT FROM 'Available' AND rate_card_id IS NULL AND rate_card_version IS NULL AND rate_card_pin_hash IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_operations_shape",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                sql: "(operation_kind = 'Cancel' AND provider_revision IS NULL AND expected_provider_revision IS NULL AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NULL AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash IS NULL AND provider_state IS NULL) OR (operation_kind = 'Publish' AND provider_revision > 0 AND expected_provider_revision > 0 AND provider_revision = expected_provider_revision + 1 AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NOT NULL AND credential_secret_id IS NOT NULL AND credential_version ~ '^[0-9a-fA-F]{32}$' AND configuration_hash ~ '^[0-9a-f]{64}$' AND (provider_state IS NULL OR provider_state IN ('Draft', 'Available'))) OR (operation_kind = 'Disable' AND provider_revision > 0 AND original_provider_revision > 0 AND provider_revision = original_provider_revision + 1 AND original_credential_revision > 0 AND (expected_provider_revision IS NULL OR expected_provider_revision = original_provider_revision) AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash ~ '^[0-9a-f]{64}$' AND (provider_state IS NULL OR provider_state = 'Draft'))");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION projects_config.check_model_provider_pair()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                DECLARE
                    h projects_config.model_providers%ROWTYPE;
                    r projects_config.model_provider_revisions%ROWTYPE;
                    previous projects_config.model_provider_revisions%ROWTYPE;
                    op projects_config.model_provider_credential_operations%ROWTYPE;
                    transaction_id text := pg_catalog.pg_current_xact_id()::text;
                BEGIN
                    IF TG_TABLE_NAME = 'model_providers' THEN
                        SELECT * INTO h FROM projects_config.model_providers
                        WHERE provider_id = NEW.provider_id;
                        SELECT * INTO r FROM projects_config.model_provider_revisions
                        WHERE provider_id = NEW.provider_id AND provider_revision = NEW.current_revision;
                        IF h.provider_id IS NULL OR r.provider_id IS NULL
                           OR h.write_transaction_id <> transaction_id
                           OR r.write_transaction_id <> transaction_id
                           OR h.current_revision <> NEW.current_revision
                           OR NEW.state NOT IN ('Draft', 'Available')
                           OR (TG_OP = 'INSERT' AND
                               (NEW.state <> 'Draft' OR
                                NEW.current_revision <> 1 OR
                                ROW(NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM ROW(NEW.updated_by_actor_id, NEW.updated_at)))
                           OR (TG_OP = 'UPDATE' AND
                               (OLD.state NOT IN ('Draft', 'Available') OR
                                NEW.current_revision <> OLD.current_revision + 1 OR
                                ROW(NEW.provider_id, NEW.scope, NEW.project_id, NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM
                                ROW(OLD.provider_id, OLD.scope, OLD.project_id, OLD.created_by_actor_id, OLD.created_at))) THEN
                            RAISE EXCEPTION 'provider transition is unavailable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                        END IF;
                    ELSE
                        r := NEW;
                        SELECT * INTO h FROM projects_config.model_providers WHERE provider_id = NEW.provider_id;
                        IF h.provider_id IS NULL OR h.write_transaction_id <> transaction_id
                           OR NEW.write_transaction_id <> transaction_id
                           OR h.current_revision <> NEW.provider_revision
                           OR h.state NOT IN ('Draft', 'Available')
                           OR ROW(h.scope, h.project_id, h.display_name, h.type, h.base_url, h.deployment,
                                  h.wire_api, h.azure_api_version, h.reasoning_efforts, h.prompt_capacity_tokens,
                                  h.updated_by_actor_id, h.updated_at)
                              IS DISTINCT FROM
                              ROW(NEW.scope, NEW.project_id, NEW.display_name, NEW.type, NEW.base_url, NEW.deployment,
                                  NEW.wire_api, NEW.azure_api_version, NEW.reasoning_efforts, NEW.prompt_capacity_tokens,
                                  NEW.created_by_actor_id, NEW.created_at) THEN
                            RAISE EXCEPTION 'provider transition is unavailable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                        END IF;
                        IF NEW.provider_revision = 1 THEN
                            IF h.state <> 'Draft'
                               OR NEW.credential_version IS NOT NULL OR NEW.credential_slot_id IS NOT NULL
                               OR NEW.credential_secret_id IS NOT NULL OR NEW.rate_card_pin_json IS NOT NULL THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                            END IF;
                        ELSE
                            SELECT * INTO previous FROM projects_config.model_provider_revisions
                            WHERE provider_id = NEW.provider_id AND provider_revision = NEW.provider_revision - 1;
                            IF previous.provider_id IS NULL THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                            END IF;

                            SELECT * INTO op FROM projects_config.model_provider_credential_operations
                            WHERE operation_id = NEW.operation_id
                              AND write_transaction_id = transaction_id
                              AND provider_id = NEW.provider_id
                              AND provider_revision = NEW.provider_revision
                              AND scope = NEW.scope
                              AND project_id IS NOT DISTINCT FROM NEW.project_id;

                            IF h.state = 'Available' THEN
                                IF op.operation_id IS NULL
                                   OR op.operation_kind IS DISTINCT FROM 'Publish'
                                   OR op.provider_state IS DISTINCT FROM 'Available'
                                   OR NEW.credential_version IS NULL OR NEW.credential_slot_id IS NULL OR NEW.credential_secret_id IS NULL
                                   OR NEW.credential_version IS DISTINCT FROM op.credential_version
                                   OR NEW.credential_slot_id IS DISTINCT FROM op.credential_slot_id
                                   OR NEW.credential_secret_id IS DISTINCT FROM op.credential_secret_id
                                   OR NEW.configuration_hash IS DISTINCT FROM previous.configuration_hash
                                   OR NEW.configuration_hash IS DISTINCT FROM op.configuration_hash
                                   OR NEW.rate_card_id IS NULL OR length(NEW.rate_card_id) = 0
                                   OR NEW.rate_card_version IS NULL OR length(NEW.rate_card_version) = 0
                                   OR NEW.rate_card_id IS DISTINCT FROM previous.rate_card_id
                                   OR NEW.rate_card_version IS DISTINCT FROM previous.rate_card_version
                                   OR NEW.rate_card_pin_json IS NULL
                                   OR pg_catalog.jsonb_typeof(NEW.rate_card_pin_json) <> 'object'
                                   OR (NEW.rate_card_pin_json ->> 'rateCardId') IS DISTINCT FROM NEW.rate_card_id
                                   OR (NEW.rate_card_pin_json ->> 'rateCardVersion') IS DISTINCT FROM NEW.rate_card_version
                                   OR COALESCE(NEW.rate_card_pin_json ->> 'contentHash', '') !~ '^[0-9a-f]{64}$'
                                   OR pg_catalog.jsonb_typeof(NEW.rate_card_pin_json -> 'provenance') <> 'object'
                                   OR COALESCE(NEW.rate_card_pin_json -> 'provenance' ->> 'source', '') NOT IN ('manual', 'azure-retail-prices')
                                   OR op.rate_card_id IS DISTINCT FROM NEW.rate_card_id
                                   OR op.rate_card_version IS DISTINCT FROM NEW.rate_card_version
                                   OR op.rate_card_pin_hash IS DISTINCT FROM (NEW.rate_card_pin_json ->> 'contentHash') THEN
                                    RAISE EXCEPTION 'provider transition is unavailable'
                                        USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                                END IF;
                            ELSE
                                IF NEW.rate_card_pin_json IS NOT NULL THEN
                                    RAISE EXCEPTION 'provider transition is unavailable'
                                        USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                                END IF;
                                IF ROW(NEW.credential_version, NEW.credential_slot_id, NEW.credential_secret_id)
                                   IS DISTINCT FROM
                                   ROW(previous.credential_version, previous.credential_slot_id, previous.credential_secret_id) THEN
                                    IF op.operation_id IS NULL OR
                                       NOT ((op.operation_kind = 'Publish' AND
                                             op.provider_state IS NOT DISTINCT FROM 'Draft' AND
                                             NEW.credential_version IS NOT DISTINCT FROM op.credential_version AND
                                             NEW.credential_slot_id IS NOT DISTINCT FROM op.credential_slot_id AND
                                             NEW.credential_secret_id IS NOT DISTINCT FROM op.credential_secret_id AND
                                             NEW.configuration_hash IS NOT DISTINCT FROM op.configuration_hash) OR
                                            (op.operation_kind = 'Disable' AND
                                             op.provider_state IS NOT DISTINCT FROM 'Draft' AND
                                             NEW.credential_version IS NULL AND
                                             NEW.credential_slot_id IS NULL AND
                                             NEW.credential_secret_id IS NULL AND
                                             NEW.configuration_hash IS NOT DISTINCT FROM op.configuration_hash)) THEN
                                        RAISE EXCEPTION 'provider transition is unavailable'
                                            USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                                    END IF;
                                ELSIF previous.rate_card_pin_json IS NOT NULL THEN
                                    IF op.operation_id IS NULL
                                       OR op.operation_kind IS DISTINCT FROM 'Publish'
                                       OR op.provider_state IS DISTINCT FROM 'Draft'
                                       OR NEW.configuration_hash IS DISTINCT FROM op.configuration_hash THEN
                                        RAISE EXCEPTION 'provider transition is unavailable'
                                            USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                                    END IF;
                                END IF;
                            END IF;
                        END IF;
                    END IF;
                    RETURN NULL;
                END;
                $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE projects_config.model_providers IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE projects_config.model_provider_revisions IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE projects_config.model_provider_credential_operations IN ACCESS EXCLUSIVE MODE;
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM projects_config.model_providers WHERE state = 'Available')
                       OR EXISTS (SELECT 1 FROM projects_config.model_provider_revisions WHERE rate_card_pin_json IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM projects_config.model_provider_credential_operations WHERE provider_state = 'Available') THEN
                        RAISE EXCEPTION 'provider_availability_rollback_blocked';
                    END IF;
                END;
                $guard$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_operations_shape",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_operations_provider_state",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_model_provider_credential_operations_rate_card",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.DropColumn(
                name: "provider_state",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.DropColumn(
                name: "rate_card_id",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.DropColumn(
                name: "rate_card_pin_hash",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.DropColumn(
                name: "rate_card_version",
                schema: "projects_config",
                table: "model_provider_credential_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_model_provider_credential_operations_shape",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                sql: "(operation_kind = 'Cancel' AND provider_revision IS NULL AND expected_provider_revision IS NULL AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NULL AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash IS NULL) OR (operation_kind = 'Publish' AND provider_revision > 0 AND expected_provider_revision > 0 AND provider_revision = expected_provider_revision + 1 AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NOT NULL AND credential_secret_id IS NOT NULL AND credential_version ~ '^[0-9a-fA-F]{32}$' AND configuration_hash ~ '^[0-9a-f]{64}$') OR (operation_kind = 'Disable' AND provider_revision > 0 AND original_provider_revision > 0 AND provider_revision = original_provider_revision + 1 AND original_credential_revision > 0 AND (expected_provider_revision IS NULL OR expected_provider_revision = original_provider_revision) AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash ~ '^[0-9a-f]{64}$')");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION projects_config.check_model_provider_pair()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                DECLARE
                    h projects_config.model_providers%ROWTYPE;
                    r projects_config.model_provider_revisions%ROWTYPE;
                    previous projects_config.model_provider_revisions%ROWTYPE;
                    op projects_config.model_provider_credential_operations%ROWTYPE;
                    transaction_id text := pg_catalog.pg_current_xact_id()::text;
                BEGIN
                    IF TG_TABLE_NAME = 'model_providers' THEN
                        SELECT * INTO h FROM projects_config.model_providers
                        WHERE provider_id = NEW.provider_id;
                        SELECT * INTO r FROM projects_config.model_provider_revisions
                        WHERE provider_id = NEW.provider_id AND provider_revision = NEW.current_revision;
                        IF h.provider_id IS NULL OR r.provider_id IS NULL
                           OR h.write_transaction_id <> transaction_id
                           OR r.write_transaction_id <> transaction_id
                           OR h.current_revision <> NEW.current_revision
                           OR NEW.state NOT IN ('Draft', 'Available')
                           OR (TG_OP = 'INSERT' AND
                               (NEW.state <> 'Draft' OR
                                NEW.current_revision <> 1 OR
                                ROW(NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM ROW(NEW.updated_by_actor_id, NEW.updated_at)))
                           OR (TG_OP = 'UPDATE' AND
                               (OLD.state NOT IN ('Draft', 'Available') OR
                                NEW.state <> OLD.state OR
                                NEW.current_revision <> OLD.current_revision + 1 OR
                                ROW(NEW.provider_id, NEW.scope, NEW.project_id, NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM
                                ROW(OLD.provider_id, OLD.scope, OLD.project_id, OLD.created_by_actor_id, OLD.created_at))) THEN
                            RAISE EXCEPTION 'provider transition is unavailable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                        END IF;
                    ELSE
                        r := NEW;
                        SELECT * INTO h FROM projects_config.model_providers WHERE provider_id = NEW.provider_id;
                        IF h.provider_id IS NULL OR h.write_transaction_id <> transaction_id
                           OR NEW.write_transaction_id <> transaction_id
                           OR h.current_revision <> NEW.provider_revision
                           OR h.state NOT IN ('Draft', 'Available')
                           OR ROW(h.scope, h.project_id, h.display_name, h.type, h.base_url, h.deployment,
                                  h.wire_api, h.azure_api_version, h.reasoning_efforts, h.prompt_capacity_tokens,
                                  h.updated_by_actor_id, h.updated_at)
                              IS DISTINCT FROM
                              ROW(NEW.scope, NEW.project_id, NEW.display_name, NEW.type, NEW.base_url, NEW.deployment,
                                  NEW.wire_api, NEW.azure_api_version, NEW.reasoning_efforts, NEW.prompt_capacity_tokens,
                                  NEW.created_by_actor_id, NEW.created_at) THEN
                            RAISE EXCEPTION 'provider transition is unavailable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                        END IF;
                        IF NEW.provider_revision = 1 THEN
                            IF NEW.credential_version IS NOT NULL OR NEW.credential_slot_id IS NOT NULL
                               OR NEW.credential_secret_id IS NOT NULL THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                            END IF;
                        ELSE
                            SELECT * INTO previous FROM projects_config.model_provider_revisions
                            WHERE provider_id = NEW.provider_id AND provider_revision = NEW.provider_revision - 1;
                            IF previous.provider_id IS NULL THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                            END IF;
                            IF ROW(NEW.credential_version, NEW.credential_slot_id, NEW.credential_secret_id)
                               IS DISTINCT FROM
                               ROW(previous.credential_version, previous.credential_slot_id, previous.credential_secret_id) THEN
                                SELECT * INTO op FROM projects_config.model_provider_credential_operations
                                WHERE operation_id = NEW.operation_id
                                  AND write_transaction_id = transaction_id
                                  AND provider_id = NEW.provider_id
                                  AND provider_revision = NEW.provider_revision
                                  AND scope = NEW.scope
                                  AND project_id IS NOT DISTINCT FROM NEW.project_id;
                                IF op.operation_id IS NULL OR
                                   NOT ((op.operation_kind = 'Publish' AND
                                         NEW.credential_version = op.credential_version AND
                                         NEW.credential_slot_id = op.credential_slot_id AND
                                         NEW.credential_secret_id = op.credential_secret_id) OR
                                        (op.operation_kind = 'Disable' AND
                                         NEW.credential_version IS NULL AND
                                         NEW.credential_slot_id IS NULL AND
                                         NEW.credential_secret_id IS NULL)) THEN
                                    RAISE EXCEPTION 'provider transition is unavailable'
                                        USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                                END IF;
                            END IF;
                        END IF;
                    END IF;
                    RETURN NULL;
                END;
                $function$;
                """);
        }
    }
}
