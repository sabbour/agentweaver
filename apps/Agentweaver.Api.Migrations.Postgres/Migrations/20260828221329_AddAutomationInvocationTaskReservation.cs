using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260828221329_AddAutomationInvocationTaskReservation")]
    public partial class AddAutomationInvocationTaskReservation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_backlog_task_id",
                table: "automation_invocations",
                type: "text",
                nullable: true);

            // Only a server-owned, still-staged task whose occurrence identity is exact can be
            // recovered as a pre-reservation handoff. Published tasks and unrelated backlog work
            // intentionally remain untouched.
            migrationBuilder.Sql(
                """
                UPDATE automation_invocations AS invocation
                   SET pending_backlog_task_id = invocation.backlog_task_id
                  FROM backlog_tasks AS task
                 WHERE invocation.pending_backlog_task_id IS NULL
                   AND invocation.backlog_task_id IS NOT NULL
                   AND invocation.outcome = 0
                   AND task.task_id = invocation.backlog_task_id
                   AND task.project_id = invocation.project_id
                   AND task.state = 'backlog'
                   AND task.run_id IS NULL
                   AND task.archived_at IS NULL
                   AND task.automation_invocation_pending = TRUE
                   AND task.source_file_path = invocation.occurrence_key;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_automation_invocations_pending_backlog_task_id",
                table: "automation_invocations",
                column: "pending_backlog_task_id",
                unique: true,
                filter: "pending_backlog_task_id IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_automation_invocations_pending_backlog_task_id",
                table: "automation_invocations");

            migrationBuilder.DropColumn(
                name: "pending_backlog_task_id",
                table: "automation_invocations");
        }
    }
}
