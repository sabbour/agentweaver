using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260929000000_PinAssemblyRevisionInput")]
public sealed class PinAssemblyRevisionInput : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("RevisionInputCommitHash", "Subtasks", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("RevisionInputRevisionId", "Subtasks", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("RevisionInputCommitHash", "Subtasks");
        migrationBuilder.DropColumn("RevisionInputRevisionId", "Subtasks");
    }
}
