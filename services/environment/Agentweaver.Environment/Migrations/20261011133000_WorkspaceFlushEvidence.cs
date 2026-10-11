using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261011133000_WorkspaceFlushEvidence")]
public sealed class WorkspaceFlushEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(
            name: "flush_evidence_json",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            type: "jsonb",
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "flush_evidence_json",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects");
}
