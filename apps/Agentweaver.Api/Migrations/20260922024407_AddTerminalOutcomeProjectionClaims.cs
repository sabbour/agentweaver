using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTerminalOutcomeProjectionClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "terminal_run_outcome_projections",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "TEXT", nullable: false),
                    lifecycle_generation = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_terminal_run_outcome_projections", x => new { x.run_id, x.lifecycle_generation });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "terminal_run_outcome_projections");
        }
    }
}
