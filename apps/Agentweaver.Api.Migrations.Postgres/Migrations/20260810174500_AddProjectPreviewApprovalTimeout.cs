using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260810174500_AddProjectPreviewApprovalTimeout")]
public sealed class AddProjectPreviewApprovalTimeout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "preview_approval_timeout_minutes",
            table: "projects",
            type: "integer",
            nullable: false,
            defaultValue: 30);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "preview_approval_timeout_minutes",
            table: "projects");
    }
}
