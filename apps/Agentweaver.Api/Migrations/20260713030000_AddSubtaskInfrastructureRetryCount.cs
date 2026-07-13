using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260713030000_AddSubtaskInfrastructureRetryCount")]
public partial class AddSubtaskInfrastructureRetryCount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "InfrastructureRetryCount",
            table: "Subtasks",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "InfrastructureRetryEligibleAt",
            table: "Subtasks",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "InfrastructureRetryCount",
            table: "Subtasks");

        migrationBuilder.DropColumn(
            name: "InfrastructureRetryEligibleAt",
            table: "Subtasks");
    }
}
