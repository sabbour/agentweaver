using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOAuthBrokerState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpAuthorizationCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    GithubLogin = table.Column<string>(type: "TEXT", nullable: false),
                    CodeChallenge = table.Column<string>(type: "TEXT", nullable: false),
                    RedirectUri = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAuthorizationCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpPendingAuthorizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    RedirectUri = table.Column<string>(type: "TEXT", nullable: false),
                    CodeChallenge = table.Column<string>(type: "TEXT", nullable: false),
                    ClientState = table.Column<string>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Resource = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpPendingAuthorizations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpAuthorizationCodes_Code",
                table: "McpAuthorizationCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpAuthorizationCodes_ExpiresAt",
                table: "McpAuthorizationCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_McpPendingAuthorizations_ExpiresAt",
                table: "McpPendingAuthorizations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_McpPendingAuthorizations_State",
                table: "McpPendingAuthorizations",
                column: "State",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpAuthorizationCodes");

            migrationBuilder.DropTable(
                name: "McpPendingAuthorizations");
        }
    }
}
