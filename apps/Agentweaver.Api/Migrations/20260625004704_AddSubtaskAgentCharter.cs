using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubtaskAgentCharter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentCharter",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentCharter",
                table: "Subtasks");
        }
    }
}
