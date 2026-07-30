using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260729220000_AddProjectRoleAssignments")]
public sealed class AddProjectRoleAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "project_role_assignments",
            columns: table => new
            {
                project_id = table.Column<string>(nullable: false),
                principal_id = table.Column<string>(nullable: false),
                role = table.Column<string>(nullable: false),
                granted_by = table.Column<string>(nullable: false),
                granted_at = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_role_assignments", x => new { x.project_id, x.principal_id });
                table.ForeignKey(
                    name: "FK_project_role_assignments_projects_project_id",
                    column: x => x.project_id,
                    principalTable: "projects",
                    principalColumn: "project_id",
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint(
                    "CK_project_role_assignments_role",
                    "role IN ('Owner', 'Contributor', 'Viewer')");
            });

        migrationBuilder.CreateIndex(
            name: "IX_project_role_assignments_principal_id",
            table: "project_role_assignments",
            column: "principal_id");

        migrationBuilder.CreateIndex(
            name: "IX_project_role_assignments_project_role",
            table: "project_role_assignments",
            columns: new[] { "project_id", "role" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "project_role_assignments");
    }
}
