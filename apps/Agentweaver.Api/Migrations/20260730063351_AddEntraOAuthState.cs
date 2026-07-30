using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEntraOAuthState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntraOAuthStates",
                columns: table => new
                {
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CodeVerifier = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntraOAuthStates", x => x.State);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntraOAuthStates_ExpiresAt",
                table: "EntraOAuthStates",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntraOAuthStates");
        }
    }
}
