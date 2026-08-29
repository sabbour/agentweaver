using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubRepositorySelectionCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_repository_selection_codes",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    entra_object_id = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    consumed_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_repository_selection_codes", x => x.code_hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_selection_codes_entra_object_id_expires_a~",
                table: "github_repository_selection_codes",
                columns: new[] { "entra_object_id", "expires_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_selection_codes_expires_at_unix_ms",
                table: "github_repository_selection_codes",
                column: "expires_at_unix_ms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_repository_selection_codes");
        }
    }
}
