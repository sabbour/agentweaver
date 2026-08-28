using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpHandoffBrowserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "browser_session_id",
                table: "github_authorizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BrowserEntraSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EntraObjectId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrowserEntraSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrowserEntraSessions_ExpiresAt",
                table: "BrowserEntraSessions",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrowserEntraSessions");

            migrationBuilder.DropColumn(
                name: "browser_session_id",
                table: "github_authorizations");
        }
    }
}
