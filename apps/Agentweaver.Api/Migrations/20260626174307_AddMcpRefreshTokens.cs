using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ChainId = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    GithubLogin = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Org = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpRefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpRevokedJtis",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Jti = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpRevokedJtis", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpRefreshTokens_ChainId",
                table: "McpRefreshTokens",
                column: "ChainId");

            migrationBuilder.CreateIndex(
                name: "IX_McpRefreshTokens_Subject_ClientId",
                table: "McpRefreshTokens",
                columns: new[] { "Subject", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_McpRefreshTokens_TokenHash",
                table: "McpRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpRevokedJtis_ExpiresAt",
                table: "McpRevokedJtis",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_McpRevokedJtis_Jti",
                table: "McpRevokedJtis",
                column: "Jti",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpRefreshTokens");

            migrationBuilder.DropTable(
                name: "McpRevokedJtis");
        }
    }
}
