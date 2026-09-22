using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

/// <inheritdoc />
public partial class AddTerminalRunOutcomes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "lifecycle_generation",
            table: "runs",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.CreateTable(
            name: "terminal_run_outcomes",
            columns: table => new
            {
                run_id = table.Column<string>(type: "text", nullable: false),
                lifecycle_generation = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                event_type = table.Column<string>(type: "text", nullable: false),
                payload_json = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                projected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_terminal_run_outcomes",
                    x => new { x.run_id, x.lifecycle_generation });
            });

        migrationBuilder.CreateIndex(
            name: "IX_terminal_run_outcomes_unprojected",
            table: "terminal_run_outcomes",
            columns: new[] { "projected_at", "occurred_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "terminal_run_outcomes");
        migrationBuilder.DropColumn(name: "lifecycle_generation", table: "runs");
    }
}
