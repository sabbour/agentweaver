using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class QueueWorkflowDefinitionSnapshotPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "workflow_definition_snapshot_yaml",
                table: "backlog_tasks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "workflow_definition_snapshot_yaml",
                table: "backlog_tasks");
        }
    }
}
