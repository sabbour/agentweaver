using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSetupOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_setup_operations",
                schema: "projects_config",
                columns: table => new
                {
                    invocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", maxLength: 64, nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    project_revision = table.Column<long>(type: "bigint", nullable: false),
                    project_configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    expected_project_revision = table.Column<long>(type: "bigint", nullable: true),
                    expected_assignment_revision = table.Column<long>(type: "bigint", nullable: true),
                    authority_audit_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_setup_operations", x => new { x.invocation_id, x.operation_kind });
                    table.CheckConstraint("ck_project_setup_operations_cleanup_revision", "(operation_kind = 'Setup' AND result_state = 'Created' AND expected_project_revision IS NULL AND expected_assignment_revision IS NULL) OR (operation_kind = 'Cleanup' AND result_state IN ('Cleaned', 'RetainedPending') AND expected_project_revision IS NOT NULL AND expected_project_revision > 0 AND expected_assignment_revision IS NOT NULL AND expected_assignment_revision > 0)");
                    table.CheckConstraint("ck_project_setup_operations_hash", "request_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_project_setup_operations_kind", "operation_kind IN ('Setup', 'Cleanup')");
                    table.CheckConstraint("ck_project_setup_operations_revision", "membership_revision > 0 AND project_revision > 0 AND project_configuration_revision > 0 AND assignment_revision > 0");
                    table.CheckConstraint("ck_project_setup_operations_state", "result_state IN ('Created', 'Cleaned', 'RetainedPending')");
                    table.ForeignKey(
                        name: "FK_project_setup_operations_authority_audit_authority_audit_ev~",
                        column: x => x.authority_audit_event_id,
                        principalSchema: "projects_config",
                        principalTable: "authority_audit",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_setup_operations_project_role_assignments_assignmen~",
                        column: x => x.assignment_id,
                        principalSchema: "projects_config",
                        principalTable: "project_role_assignments",
                        principalColumn: "assignment_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_setup_operations_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_setup_operations_tenant_memberships_membership_id",
                        column: x => x.membership_id,
                        principalSchema: "projects_config",
                        principalTable: "tenant_memberships",
                        principalColumn: "membership_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_assignment_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_authority_audit_event_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "authority_audit_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_membership_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "membership_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_setup_operations_project_id",
                schema: "projects_config",
                table: "project_setup_operations",
                column: "project_id");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION projects_config.reject_project_setup_operation_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'project setup operation records are immutable'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER project_setup_operations_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_setup_operations
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_project_setup_operation_mutation();

                CREATE TRIGGER project_setup_operations_no_truncate
                BEFORE TRUNCATE ON projects_config.project_setup_operations
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_project_setup_operation_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS project_setup_operations_immutable
                    ON projects_config.project_setup_operations;
                DROP TRIGGER IF EXISTS project_setup_operations_no_truncate
                    ON projects_config.project_setup_operations;
                DROP FUNCTION IF EXISTS projects_config.reject_project_setup_operation_mutation();
                """);

            migrationBuilder.DropTable(
                name: "project_setup_operations",
                schema: "projects_config");
        }
    }
}
