using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260716090000_AddBacklogTaskPromotion")]
public partial class AddBacklogTaskPromotion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "parent_prd_run_id",
            table: "backlog_tasks",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "promotion_key",
            table: "backlog_tasks",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "promotion_reason",
            table: "backlog_tasks",
            type: "text",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_backlog_tasks_parent_promotion_key",
            table: "backlog_tasks",
            columns: new[] { "parent_prd_run_id", "promotion_key" },
            unique: true,
            filter: "parent_prd_run_id IS NOT NULL AND promotion_key IS NOT NULL");

        migrationBuilder.CreateTable(
            name: "backlog_task_dependencies",
            columns: table => new
            {
                project_id = table.Column<string>(type: "text", nullable: false),
                task_id = table.Column<string>(type: "text", nullable: false),
                depends_on_task_id = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backlog_task_dependencies", x => new { x.task_id, x.depends_on_task_id });
                table.ForeignKey(
                    name: "FK_backlog_task_dependencies_backlog_tasks_depends_on_task_id",
                    column: x => x.depends_on_task_id,
                    principalTable: "backlog_tasks",
                    principalColumn: "task_id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_backlog_task_dependencies_backlog_tasks_task_id",
                    column: x => x.task_id,
                    principalTable: "backlog_tasks",
                    principalColumn: "task_id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_backlog_task_dependencies_project_task",
            table: "backlog_task_dependencies",
            columns: new[] { "project_id", "task_id" });

        migrationBuilder.CreateIndex(
            name: "IX_backlog_task_dependencies_prerequisite",
            table: "backlog_task_dependencies",
            column: "depends_on_task_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "backlog_task_dependencies");

        migrationBuilder.DropIndex(
            name: "IX_backlog_tasks_parent_promotion_key",
            table: "backlog_tasks");

        migrationBuilder.DropColumn(
            name: "parent_prd_run_id",
            table: "backlog_tasks");

        migrationBuilder.DropColumn(
            name: "promotion_key",
            table: "backlog_tasks");

        migrationBuilder.DropColumn(
            name: "promotion_reason",
            table: "backlog_tasks");
    }
}
