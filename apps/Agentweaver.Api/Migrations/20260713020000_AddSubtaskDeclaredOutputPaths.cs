using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260713020000_AddSubtaskDeclaredOutputPaths")]
public partial class AddSubtaskDeclaredOutputPaths : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DeclaredOutputPathsJson",
            table: "Subtasks",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DeclaredOutputPathsJson",
            table: "Subtasks");
    }
}
