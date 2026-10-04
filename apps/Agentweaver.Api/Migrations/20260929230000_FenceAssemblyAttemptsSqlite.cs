using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Api.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260929230000_FenceAssemblyAttemptsSqlite")]
public sealed class FenceAssemblyAttemptsSqlite : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AssemblyFencingToken",
            table: "WorkPlans",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AssemblyFencingToken",
            table: "WorkPlans");
    }
}
