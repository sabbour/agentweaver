using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProjectGitHubIdentityOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_github_identity_overrides");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_github_identity_overrides",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    entra_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    github_login = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_github_identity_overrides", x => new { x.project_id, x.entra_user_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_github_identity_overrides_user_login",
                table: "project_github_identity_overrides",
                columns: new[] { "entra_user_id", "github_login" });
        }
    }
}
