using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedGitHubAccountStateAndProjectOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_account_link_states",
                columns: table => new
                {
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    entra_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_account_link_states", x => x.state);
                });

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
                name: "IX_github_account_link_states_expires_at",
                table: "github_account_link_states",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_project_github_identity_overrides_user_login",
                table: "project_github_identity_overrides",
                columns: new[] { "entra_user_id", "github_login" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_account_link_states");

            migrationBuilder.DropTable(
                name: "project_github_identity_overrides");
        }
    }
}
