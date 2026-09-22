using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations;

public partial class AddTerminalProjectionEventSequence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>(
            name: "event_sequence",
            table: "terminal_run_outcome_projections",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "event_sequence", table: "terminal_run_outcome_projections");
}
