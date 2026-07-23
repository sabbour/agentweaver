using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260723180000_AddAgentMemoryProvenance")]
public partial class AddAgentMemoryProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Provenance",
            table: "AgentMemory",
            type: "TEXT",
            nullable: false,
            defaultValue: MemoryProvenance.AgentAuthored);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Provenance",
            table: "AgentMemory");
    }
}
