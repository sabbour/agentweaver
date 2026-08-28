using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260828172800_AddMcpHandoffBrowserSessions")]
public partial class AddMcpHandoffBrowserSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "browser_session_id",
            table: "github_authorizations",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "BrowserEntraSessions",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                EntraObjectId = table.Column<string>(type: "text", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_BrowserEntraSessions", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_BrowserEntraSessions_ExpiresAt",
            table: "BrowserEntraSessions",
            column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BrowserEntraSessions");
        migrationBuilder.DropColumn(name: "browser_session_id", table: "github_authorizations");
    }
}
