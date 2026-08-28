using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationInvocationBacklogBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "backlog_task_id",
                table: "automation_invocations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_automation_invocations_backlog_task_id",
                table: "automation_invocations",
                column: "backlog_task_id",
                unique: true,
                filter: "backlog_task_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_automation_invocations_backlog_task_id",
                table: "automation_invocations");

            migrationBuilder.DropColumn(
                name: "backlog_task_id",
                table: "automation_invocations");
        }
    }
}
