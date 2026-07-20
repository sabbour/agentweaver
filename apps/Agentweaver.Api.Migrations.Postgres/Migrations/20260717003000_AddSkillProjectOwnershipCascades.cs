using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260717003000_AddSkillProjectOwnershipCascades")]
public sealed class AddSkillProjectOwnershipCascades : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM skill_assignments AS assignment
             WHERE NOT EXISTS (
                       SELECT 1
                         FROM projects AS project
                        WHERE project.project_id = assignment.project_id)
                OR NOT EXISTS (
                       SELECT 1
                         FROM skills AS skill
                        WHERE skill.project_id = assignment.project_id
                          AND skill.skill_id = assignment.skill_id);

            DELETE FROM skills AS skill
             WHERE NOT EXISTS (
                       SELECT 1
                         FROM projects AS project
                        WHERE project.project_id = skill.project_id);
            """);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_skills_project_id_skill_id",
            table: "skills",
            columns: new[] { "project_id", "skill_id" });

        migrationBuilder.AddForeignKey(
            name: "FK_skills_projects_project_id",
            table: "skills",
            column: "project_id",
            principalTable: "projects",
            principalColumn: "project_id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_skill_assignments_projects_project_id",
            table: "skill_assignments",
            column: "project_id",
            principalTable: "projects",
            principalColumn: "project_id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.AddForeignKey(
            name: "FK_skill_assignments_skills_project_id_skill_id",
            table: "skill_assignments",
            columns: new[] { "project_id", "skill_id" },
            principalTable: "skills",
            principalColumns: new[] { "project_id", "skill_id" },
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_skill_assignments_projects_project_id",
            table: "skill_assignments");
        migrationBuilder.DropForeignKey(
            name: "FK_skill_assignments_skills_project_id_skill_id",
            table: "skill_assignments");
        migrationBuilder.DropForeignKey(
            name: "FK_skills_projects_project_id",
            table: "skills");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_skills_project_id_skill_id",
            table: "skills");
    }
}
