using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260723000000_AddProjectWebhookSecret")]
public sealed class AddProjectWebhookSecret : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "webhook_secret",
            table: "projects",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "webhook_secret",
            table: "projects");
    }
}
