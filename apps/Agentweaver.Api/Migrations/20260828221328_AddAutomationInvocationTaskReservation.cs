using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationInvocationTaskReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite keeps invocation and backlog data in separate databases, so the exact-match
            // legacy backfill runs at recovery time after both stores are available.
            migrationBuilder.AddColumn<string>(
                name: "pending_backlog_task_id",
                table: "automation_invocations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_automation_invocations_pending_backlog_task_id",
                table: "automation_invocations",
                column: "pending_backlog_task_id",
                unique: true,
                filter: "pending_backlog_task_id IS NOT NULL");
        }

        /// <inheritdoc />
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
