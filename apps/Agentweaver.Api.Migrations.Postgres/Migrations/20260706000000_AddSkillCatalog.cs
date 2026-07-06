using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260706000000_AddSkillCatalog")]
    public partial class AddSkillCatalog : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    skill_id = table.Column<string>(nullable: false),
                    project_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    description = table.Column<string>(nullable: false),
                    instructions = table.Column<string>(nullable: false),
                    resources = table.Column<string>(nullable: true),
                    provenance = table.Column<string>(nullable: false),
                    source_repository = table.Column<string>(nullable: true),
                    source_location = table.Column<string>(nullable: true),
                    content_hash = table.Column<string>(nullable: false),
                    status = table.Column<string>(nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.skill_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skills_project_name",
                table: "skills",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "skill_assignments",
                columns: table => new
                {
                    project_id = table.Column<string>(nullable: false),
                    skill_id = table.Column<string>(nullable: false),
                    agent_name = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_assignments", x => new { x.project_id, x.skill_id, x.agent_name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_assignments_agent",
                table: "skill_assignments",
                columns: new[] { "project_id", "agent_name" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "skill_assignments");
            migrationBuilder.DropTable(name: "skills");
        }
    }
}
