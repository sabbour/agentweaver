using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderCredentialOperationsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_credential_operations",
                schema: "projects_config",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_revision = table.Column<long>(type: "bigint", nullable: true),
                    expected_provider_revision = table.Column<long>(type: "bigint", nullable: true),
                    original_provider_revision = table.Column<long>(type: "bigint", nullable: true),
                    original_credential_revision = table.Column<long>(type: "bigint", nullable: true),
                    credential_slot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    credential_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    original_tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    original_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    current_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    current_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    write_transaction_id = table.Column<string>(type: "text", nullable: false, defaultValue: "legacy")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_credential_operations", x => x.operation_id);
                    table.CheckConstraint("ck_model_provider_credential_operations_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_model_provider_credential_operations_kind", "operation_kind IN ('Publish', 'Cancel', 'Disable')");
                    table.CheckConstraint("ck_model_provider_credential_operations_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                    table.CheckConstraint("ck_model_provider_credential_operations_shape", "(operation_kind = 'Cancel' AND provider_revision IS NULL AND expected_provider_revision IS NULL AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NULL AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash IS NULL) OR (operation_kind = 'Publish' AND provider_revision > 0 AND expected_provider_revision > 0 AND provider_revision = expected_provider_revision + 1 AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NOT NULL AND credential_secret_id IS NOT NULL AND credential_version ~ '^[0-9a-fA-F]{32}$' AND configuration_hash ~ '^[0-9a-f]{64}$') OR (operation_kind = 'Disable' AND provider_revision > 0 AND original_provider_revision > 0 AND provider_revision = original_provider_revision + 1 AND original_credential_revision > 0 AND (expected_provider_revision IS NULL OR expected_provider_revision = original_provider_revision) AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash ~ '^[0-9a-f]{64}$')");
                });

            migrationBuilder.CreateTable(
                name: "model_provider_credential_preflights",
                schema: "projects_config",
                columns: table => new
                {
                    preflight_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_by_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_credential_preflights", x => x.preflight_id);
                    table.CheckConstraint("ck_model_provider_credential_preflights_consumed", "(consumed_at IS NULL AND consumed_by_kind IS NULL) OR (consumed_at IS NOT NULL AND consumed_by_kind IN ('Publish', 'Cancel', 'Disable'))");
                    table.CheckConstraint("ck_model_provider_credential_preflights_mode", "mode IN ('draft-snapshot', 'authority-only')");
                    table.CheckConstraint("ck_model_provider_credential_preflights_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_credential_preflights_operation_issued",
                schema: "projects_config",
                table: "model_provider_credential_preflights",
                columns: new[] { "operation_id", "issued_at" });

            migrationBuilder.Sql("""
                CREATE FUNCTION projects_config.guard_model_provider_credential_preflight_mutation()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'records in %.% are immutable', TG_TABLE_SCHEMA, TG_TABLE_NAME
                            USING ERRCODE = '55000';
                    END IF;
                    IF OLD.consumed_at IS NOT NULL OR NEW.consumed_at IS NULL OR NEW.consumed_by_kind IS NULL THEN
                        RAISE EXCEPTION 'credential preflight transition is invalid'
                            USING ERRCODE = '23514';
                    END IF;
                    IF ROW(NEW.preflight_id, NEW.operation_id, NEW.mode, NEW.scope, NEW.project_id,
                           NEW.provider_id, NEW.issuer, NEW.actor_id, NEW.tenant_id,
                           NEW.membership_id, NEW.membership_revision, NEW.assignment_id, NEW.assignment_revision,
                           NEW.issued_at)
                       IS DISTINCT FROM
                       ROW(OLD.preflight_id, OLD.operation_id, OLD.mode, OLD.scope, OLD.project_id,
                           OLD.provider_id, OLD.issuer, OLD.actor_id, OLD.tenant_id,
                           OLD.membership_id, OLD.membership_revision, OLD.assignment_id, OLD.assignment_revision,
                           OLD.issued_at) THEN
                        RAISE EXCEPTION 'credential preflight identity is immutable'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.guard_model_provider_credential_preflight_mutation() FROM PUBLIC;

                CREATE TRIGGER model_provider_credential_preflights_consume_only
                BEFORE UPDATE OR DELETE ON projects_config.model_provider_credential_preflights
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_model_provider_credential_preflight_mutation();

                CREATE TRIGGER model_provider_credential_preflights_no_truncate
                BEFORE TRUNCATE ON projects_config.model_provider_credential_preflights
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE FUNCTION projects_config.lock_credential_publication_authority(
                    p_membership_id uuid,
                    p_issuer text,
                    p_subject text,
                    p_tenant_id text,
                    p_membership_revision bigint,
                    p_assignment_id uuid,
                    p_assignment_revision bigint,
                    p_scope text,
                    p_project_id text)
                RETURNS text
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog
                AS $function$
                DECLARE
                    v_assignment_resource_type text;
                    v_project_tenant_id text;
                BEGIN
                    IF p_membership_id IS NULL
                        OR p_issuer IS NULL
                        OR p_subject IS NULL
                        OR p_tenant_id IS NULL
                        OR p_membership_revision IS NULL OR p_membership_revision <= 0
                        OR p_assignment_id IS NULL
                        OR p_assignment_revision IS NULL OR p_assignment_revision <= 0
                        OR p_scope IS NULL OR p_scope NOT IN ('Platform', 'Project')
                        OR (p_scope = 'Project' AND p_project_id IS NULL)
                        OR (p_scope = 'Platform' AND p_project_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'credential publication authority lock arguments are invalid'
                            USING ERRCODE = '22004';
                    END IF;

                    PERFORM 1
                    FROM projects_config.tenant_memberships AS membership
                    WHERE membership.membership_id = p_membership_id
                      AND membership.issuer = p_issuer
                      AND membership.subject = p_subject
                      AND membership.tenant_id = p_tenant_id
                      AND membership.revision = p_membership_revision
                      AND membership.state = 'Active'
                    FOR SHARE NOWAIT;
                    IF NOT FOUND THEN
                        RETURN 'membership_missing';
                    END IF;

                    IF p_scope = 'Project' THEN
                        SELECT project.tenant_id INTO v_project_tenant_id
                        FROM projects_config.projects AS project
                        WHERE project.project_id = p_project_id
                          AND project.state = 'Active'
                        FOR SHARE NOWAIT;
                        IF NOT FOUND THEN
                            RETURN 'project_missing';
                        END IF;
                    END IF;

                    SELECT assignment.resource_type INTO v_assignment_resource_type
                    FROM projects_config.project_role_assignments AS assignment
                    WHERE assignment.assignment_id = p_assignment_id
                      AND assignment.membership_id = p_membership_id
                      AND assignment.revision = p_assignment_revision
                      AND assignment.state = 'Active'
                      AND (
                          (assignment.resource_type = 'Platform'
                              AND assignment.resource_id = 'default'
                              AND assignment.role = 'PlatformAdmin')
                          OR
                          (p_scope = 'Project'
                              AND assignment.resource_type = 'Project'
                              AND assignment.resource_id = p_project_id
                              AND assignment.role = 'Owner')
                      )
                    FOR SHARE NOWAIT;
                    IF NOT FOUND THEN
                        RETURN 'assignment_missing';
                    END IF;

                    IF p_scope = 'Project'
                        AND v_assignment_resource_type = 'Project'
                        AND v_project_tenant_id IS DISTINCT FROM p_tenant_id THEN
                        RETURN 'project_missing';
                    END IF;

                    RETURN 'authorized';
                END;
                $function$;

                REVOKE ALL ON FUNCTION projects_config.lock_credential_publication_authority(
                    uuid, text, text, text, bigint, uuid, bigint, text, text) FROM PUBLIC;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_credential_operations_provider_recorded",
                schema: "projects_config",
                table: "model_provider_credential_operations",
                columns: new[] { "provider_id", "recorded_at" });

            migrationBuilder.Sql("""
                CREATE TRIGGER model_provider_credential_operations_write_marker
                BEFORE INSERT ON projects_config.model_provider_credential_operations
                FOR EACH ROW EXECUTE FUNCTION projects_config.mark_model_provider_write();

                CREATE TRIGGER model_provider_credential_operations_immutable
                BEFORE UPDATE OR DELETE ON projects_config.model_provider_credential_operations
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER model_provider_credential_operations_no_truncate
                BEFORE TRUNCATE ON projects_config.model_provider_credential_operations
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE projects_config.model_providers IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE projects_config.model_provider_revisions IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE projects_config.model_provider_credential_operations IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE projects_config.model_provider_credential_preflights IN ACCESS EXCLUSIVE MODE;
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM projects_config.model_provider_credential_operations)
                       OR EXISTS (SELECT 1 FROM projects_config.model_provider_credential_preflights)
                       OR EXISTS (SELECT 1 FROM projects_config.model_providers WHERE state <> 'Draft')
                       OR EXISTS (SELECT 1 FROM projects_config.model_provider_revisions
                                  WHERE credential_version IS NOT NULL
                                     OR credential_slot_id IS NOT NULL
                                     OR credential_secret_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'credential_operations_v2_rollback_blocked';
                    END IF;
                END;
                $guard$;
                """);

            migrationBuilder.DropTable(
                name: "model_provider_credential_preflights",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "model_provider_credential_operations",
                schema: "projects_config");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS projects_config.lock_credential_publication_authority(
                    uuid, text, text, text, bigint, uuid, bigint, text, text);
                DROP FUNCTION projects_config.guard_model_provider_credential_preflight_mutation();

                CREATE OR REPLACE FUNCTION projects_config.check_model_provider_pair()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                DECLARE
                    h projects_config.model_providers%ROWTYPE;
                    r projects_config.model_provider_revisions%ROWTYPE;
                    previous projects_config.model_provider_revisions%ROWTYPE;
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
                           OR NEW.state <> 'Draft'
                           OR (TG_OP = 'INSERT' AND
                               (NEW.current_revision <> 1 OR
                                ROW(NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM ROW(NEW.updated_by_actor_id, NEW.updated_at)))
                           OR (TG_OP = 'UPDATE' AND
                               (OLD.state <> 'Draft' OR
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
                           OR h.state <> 'Draft'
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
                            IF previous.provider_id IS NULL OR
                               ROW(NEW.credential_version, NEW.credential_slot_id, NEW.credential_secret_id)
                               IS DISTINCT FROM
                               ROW(previous.credential_version, previous.credential_slot_id, previous.credential_secret_id) THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
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
