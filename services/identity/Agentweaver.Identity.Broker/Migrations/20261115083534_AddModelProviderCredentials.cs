using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_credentials",
                schema: "identity_broker",
                columns: table => new
                {
                    credential_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    vault_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    vault_uri = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    secret_name = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    current_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    set_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_provider_credentials", x => x.credential_slot_id);
                    table.CheckConstraint("ck_model_provider_credentials_byok_name", "vault_source <> 'Byok' OR secret_name = 'byok-' || lower(scope) || '-' || provider_id");
                    table.CheckConstraint("ck_model_provider_credentials_revision", "revision > 0");
                    table.CheckConstraint("ck_model_provider_credentials_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL AND length(project_id) > 0)");
                    table.CheckConstraint("ck_model_provider_credentials_secret_name", "secret_name ~ '^[A-Za-z0-9-]{1,127}$'");
                    table.CheckConstraint("ck_model_provider_credentials_secret_version", "current_version ~ '^[0-9A-Fa-f]{32}$'");
                    table.CheckConstraint("ck_model_provider_credentials_state", "state IN ('Enabled', 'Disabled')");
                    table.CheckConstraint("ck_model_provider_credentials_vault_source", "vault_source IN ('Existing', 'Byok')");
                });

            migrationBuilder.CreateTable(
                name: "model_provider_credential_revisions",
                schema: "identity_broker",
                columns: table => new
                {
                    credential_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    vault_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    vault_uri = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    secret_name = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    secret_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mutation_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_provider_credential_revisions", x => new { x.credential_slot_id, x.revision });
                    table.CheckConstraint("ck_model_provider_credential_revisions_byok_name", "vault_source <> 'Byok' OR secret_name = 'byok-' || lower(scope) || '-' || provider_id");
                    table.CheckConstraint("ck_model_provider_credential_revisions_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_model_provider_credential_revisions_mutation_kind", "mutation_kind IN ('RecordReference', 'Disable')");
                    table.CheckConstraint("ck_model_provider_credential_revisions_revision", "revision > 0");
                    table.CheckConstraint("ck_model_provider_credential_revisions_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL AND length(project_id) > 0)");
                    table.CheckConstraint("ck_model_provider_credential_revisions_secret_name", "secret_name ~ '^[A-Za-z0-9-]{1,127}$'");
                    table.CheckConstraint("ck_model_provider_credential_revisions_secret_version", "secret_version ~ '^[0-9A-Fa-f]{32}$'");
                    table.CheckConstraint("ck_model_provider_credential_revisions_state", "state IN ('Enabled', 'Disabled')");
                    table.CheckConstraint("ck_model_provider_credential_revisions_vault_source", "vault_source IN ('Existing', 'Byok')");
                    table.ForeignKey(
                        name: "FK_model_provider_credential_revisions_model_provider_credenti~",
                        column: x => x.credential_slot_id,
                        principalSchema: "identity_broker",
                        principalTable: "model_provider_credentials",
                        principalColumn: "credential_slot_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credential_revisions_operation_id",
                schema: "identity_broker",
                table: "model_provider_credential_revisions",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_credentials_provider_id",
                schema: "identity_broker",
                table: "model_provider_credentials",
                column: "provider_id",
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION identity_broker.guard_model_provider_credentials_head()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_slot_immutable';
                    END IF;
                    IF ROW(NEW.credential_slot_id, NEW.scope, NEW.project_id, NEW.provider_id,
                           NEW.vault_source, NEW.vault_uri)
                       IS DISTINCT FROM
                       ROW(OLD.credential_slot_id, OLD.scope, OLD.project_id, OLD.provider_id,
                           OLD.vault_source, OLD.vault_uri) THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_slot_identity_immutable';
                    END IF;
                    IF NEW.revision <> OLD.revision + 1 THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_revision_must_advance_once';
                    END IF;
                    IF OLD.state = 'Disabled' THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'disabled_credential_terminal';
                    END IF;
                    IF NEW.state = 'Disabled' AND
                       (NEW.secret_name <> OLD.secret_name OR NEW.current_version <> OLD.current_version) THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'disabled_credential_reference_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER model_provider_credentials_head_guard
                BEFORE UPDATE OR DELETE ON identity_broker.model_provider_credentials
                FOR EACH ROW EXECUTE FUNCTION identity_broker.guard_model_provider_credentials_head();

                CREATE FUNCTION identity_broker.reject_model_provider_credential_revision_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'credential_revision_immutable';
                    RETURN NULL;
                END;
                $function$;

                CREATE TRIGGER model_provider_credential_revisions_row_guard
                BEFORE UPDATE OR DELETE ON identity_broker.model_provider_credential_revisions
                FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_model_provider_credential_revision_change();

                CREATE TRIGGER model_provider_credential_revisions_truncate_guard
                BEFORE TRUNCATE ON identity_broker.model_provider_credential_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION identity_broker.reject_model_provider_credential_revision_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER model_provider_credential_revisions_truncate_guard
                    ON identity_broker.model_provider_credential_revisions;
                DROP TRIGGER model_provider_credential_revisions_row_guard
                    ON identity_broker.model_provider_credential_revisions;
                DROP FUNCTION identity_broker.reject_model_provider_credential_revision_change();
                DROP TRIGGER model_provider_credentials_head_guard
                    ON identity_broker.model_provider_credentials;
                DROP FUNCTION identity_broker.guard_model_provider_credentials_head();
                """);
            migrationBuilder.DropTable(
                name: "model_provider_credential_revisions",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "model_provider_credentials",
                schema: "identity_broker");
        }
    }
}
