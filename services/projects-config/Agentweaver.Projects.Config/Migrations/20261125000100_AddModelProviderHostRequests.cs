using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderHostRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_host_requests",
                schema: "projects_config",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_revision = table.Column<long>(type: "bigint", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivery_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    environment_destination_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_host_requests", x => x.request_id);
                    table.CheckConstraint("ck_model_provider_host_requests_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_model_provider_host_requests_configuration_hash", "configuration_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_model_provider_host_requests_delivery_state", "delivery_state IN ('Pending', 'Delivered', 'Failed')");
                    table.CheckConstraint("ck_model_provider_host_requests_port", "port = 443");
                    table.CheckConstraint("ck_model_provider_host_requests_revision", "provider_revision > 0");
                    table.CheckConstraint("ck_model_provider_host_requests_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_model_provider_host_requests_model_provider_revisions_provi~",
                        columns: x => new { x.provider_id, x.provider_revision },
                        principalSchema: "projects_config",
                        principalTable: "model_provider_revisions",
                        principalColumns: new[] { "provider_id", "provider_revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_provider_host_requests_outbox",
                schema: "projects_config",
                table: "model_provider_host_requests",
                columns: new[] { "delivery_state", "next_attempt_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_model_provider_host_requests_operation_id",
                schema: "projects_config",
                table: "model_provider_host_requests",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_model_provider_host_requests_provider_revision",
                schema: "projects_config",
                table: "model_provider_host_requests",
                columns: new[] { "provider_id", "provider_revision" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION projects_config.guard_model_provider_host_request_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'model provider host requests cannot be deleted'
                            USING ERRCODE = '55000';
                    END IF;

                    IF NEW.request_id IS DISTINCT FROM OLD.request_id
                        OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                        OR NEW.scope IS DISTINCT FROM OLD.scope
                        OR NEW.project_id IS DISTINCT FROM OLD.project_id
                        OR NEW.provider_id IS DISTINCT FROM OLD.provider_id
                        OR NEW.provider_revision IS DISTINCT FROM OLD.provider_revision
                        OR NEW.configuration_hash IS DISTINCT FROM OLD.configuration_hash
                        OR NEW.operation_id IS DISTINCT FROM OLD.operation_id
                        OR NEW.actor_id IS DISTINCT FROM OLD.actor_id
                        OR NEW.host IS DISTINCT FROM OLD.host
                        OR NEW.port IS DISTINCT FROM OLD.port
                        OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                        RAISE EXCEPTION 'model provider host request identity is immutable'
                            USING ERRCODE = '55000';
                    END IF;

                    IF OLD.delivery_state IN ('Delivered', 'Failed') THEN
                        RAISE EXCEPTION 'terminal model provider host request delivery state is immutable'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.guard_model_provider_host_request_mutation() FROM PUBLIC;

                CREATE TRIGGER model_provider_host_requests_guard_mutation
                BEFORE UPDATE OR DELETE ON projects_config.model_provider_host_requests
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_model_provider_host_request_mutation();

                CREATE TRIGGER model_provider_host_requests_no_truncate
                BEFORE TRUNCATE ON projects_config.model_provider_host_requests
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_provider_host_requests",
                schema: "projects_config");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS projects_config.guard_model_provider_host_request_mutation();");
        }
    }
}
