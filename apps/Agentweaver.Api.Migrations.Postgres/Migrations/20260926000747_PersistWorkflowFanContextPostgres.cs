using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PersistWorkflowFanContextPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionBaseTreeHash",
                table: "WorkPlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentTurnInputJson",
                table: "WorkPlans",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionBaseTreeHash",
                table: "WorkPlans");

            migrationBuilder.DropColumn(
                name: "ParentTurnInputJson",
                table: "WorkPlans");
        }
    }
}
