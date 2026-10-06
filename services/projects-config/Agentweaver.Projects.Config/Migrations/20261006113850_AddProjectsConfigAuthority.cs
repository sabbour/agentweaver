using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectsConfigAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_projects_tenant_id_owner_actor_id_state",
                schema: "projects_config",
                table: "projects");

            migrationBuilder.RenameColumn(
                name: "owner_actor_id",
                schema: "projects_config",
                table: "projects",
                newName: "created_by_actor_id");

            migrationBuilder.CreateTable(
                name: "authority_audit",
                schema: "projects_config",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    resource_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_audit", x => x.event_id);
                    table.CheckConstraint("ck_authority_audit_event_type", "event_type IN ('membership_granted', 'membership_revoked', 'role_assigned', 'role_revoked')");
                    table.CheckConstraint("ck_authority_audit_revision", "revision > 0");
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                schema: "projects_config",
                columns: table => new
                {
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    granted_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_memberships", x => x.membership_id);
                    table.CheckConstraint("ck_tenant_memberships_revision", "revision > 0");
                    table.CheckConstraint("ck_tenant_memberships_state", "state IN ('Active', 'Revoked')");
                });

            migrationBuilder.CreateTable(
                name: "project_role_assignments",
                schema: "projects_config",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    granted_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_role_assignments", x => x.assignment_id);
                    table.CheckConstraint("ck_project_role_assignments_resource_type", "resource_type IN ('Platform', 'Tenant', 'Project')");
                    table.CheckConstraint("ck_project_role_assignments_revision", "revision > 0");
                    table.CheckConstraint("ck_project_role_assignments_role", "role IN ('PlatformAdmin', 'TenantAdmin', 'Owner', 'Contributor', 'Viewer', 'Orchestrator')");
                    table.CheckConstraint("ck_project_role_assignments_state", "state IN ('Active', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_project_role_assignments_tenant_memberships_membership_id",
                        column: x => x.membership_id,
                        principalSchema: "projects_config",
                        principalTable: "tenant_memberships",
                        principalColumn: "membership_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_projects_tenant_id_state",
                schema: "projects_config",
                table: "projects",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_authority_audit_membership_id_created_at",
                schema: "projects_config",
                table: "authority_audit",
                columns: new[] { "membership_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_role_assignments_membership_id_resource_type_resour~",
                schema: "projects_config",
                table: "project_role_assignments",
                columns: new[] { "membership_id", "resource_type", "resource_id", "role" },
                unique: true,
                filter: "\"state\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_project_role_assignments_resource_type_resource_id_role_sta~",
                schema: "projects_config",
                table: "project_role_assignments",
                columns: new[] { "resource_type", "resource_id", "role", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_issuer_subject_state",
                schema: "projects_config",
                table: "tenant_memberships",
                columns: new[] { "issuer", "subject", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_issuer_subject_tenant_id",
                schema: "projects_config",
                table: "tenant_memberships",
                columns: new[] { "issuer", "subject", "tenant_id" },
                unique: true,
                filter: "\"state\" = 'Active'");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION projects_config.reject_authority_audit_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'authority audit records are immutable'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER authority_audit_immutable
                BEFORE UPDATE OR DELETE ON projects_config.authority_audit
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_authority_audit_mutation();

                CREATE TRIGGER authority_audit_no_truncate
                BEFORE TRUNCATE ON projects_config.authority_audit
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_authority_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS authority_audit_immutable ON projects_config.authority_audit;
                DROP TRIGGER IF EXISTS authority_audit_no_truncate ON projects_config.authority_audit;
                DROP FUNCTION IF EXISTS projects_config.reject_authority_audit_mutation();
                """);

            migrationBuilder.DropTable(
                name: "authority_audit",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_role_assignments",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "tenant_memberships",
                schema: "projects_config");

            migrationBuilder.DropIndex(
                name: "IX_projects_tenant_id_state",
                schema: "projects_config",
                table: "projects");

            migrationBuilder.RenameColumn(
                name: "created_by_actor_id",
                schema: "projects_config",
                table: "projects",
                newName: "owner_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_projects_tenant_id_owner_actor_id_state",
                schema: "projects_config",
                table: "projects",
                columns: new[] { "tenant_id", "owner_actor_id", "state" });
        }
    }
}
